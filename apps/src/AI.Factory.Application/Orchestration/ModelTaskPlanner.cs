using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// AI-backed ITaskPlanner. Requires structured JSON output, resolves the
/// model's short string task IDs into stable Guids (PlannedTask.Id stays
/// Guid-typed — it is reused directly as the future WorkItem.Id once a
/// separate persistence service exists), and validates every plan with
/// the existing TaskPlanValidator before returning it. On malformed or
/// structurally invalid output, retries with the validation error fed
/// back into the prompt, up to a configurable, bounded MaxAttempts.
/// </summary>
public sealed class ModelTaskPlanner : ITaskPlanner
{
    private const int EstimatedContextTokens = 12_000;
    private const int ContextDocumentsPerCategory = 10;
    private const string PlanningCategoryKey = "planning";
    private const string PlannerAgentName = "planner";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IContextProvider _contextProvider;
    private readonly IModelRouter _modelRouter;
    private readonly IAgentExecutor _agentExecutor;
    private readonly ModelTaskPlannerOptions _options;
    private readonly ILogger<ModelTaskPlanner> _logger;

    public ModelTaskPlanner(
        IContextProvider contextProvider,
        IModelRouter modelRouter,
        IAgentExecutor agentExecutor,
        ModelTaskPlannerOptions options,
        ILogger<ModelTaskPlanner> logger)
    {
        _contextProvider = contextProvider;
        _modelRouter = modelRouter;
        _agentExecutor = agentExecutor;
        _options = options;
        _logger = logger;
    }

    public async Task<TaskPlan> CreatePlanAsync(
        TaskPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var contextText = await BuildContextAsync(request.ProjectId, cancellationToken);

        var modelSelection = _modelRouter.SelectModel(new ModelRoutingRequest(
            PlanningCategoryKey, TaskComplexity.Complex, EstimatedContextTokens, CostPreference.BestQuality));

        _logger.LogInformation(
            "Task planning starting for project {ProjectId} using model {ModelId}",
            request.ProjectId, modelSelection.ModelId);

        string? previousError = null;
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            var prompt = BuildPrompt(request, contextText, previousError);

            var result = await _agentExecutor.ExecuteAsync(
                // Same defect the Phase 18.20 live run exposed in
                // AgentCodeReviewer: WorkingDirectory is a real filesystem path
                // (the container bind mount) since Phase 18.14, never an id.
                // Empty selects the executor's fresh isolated scratch workspace,
                // which is all a planning prompt needs.
                new AgentExecutionRequest(PlannerAgentName, modelSelection.ModelId, prompt, string.Empty),
                cancellationToken);

            if (!result.Success)
            {
                // Agent-infrastructure failure is a different class of
                // problem than malformed output — do not retry it here.
                throw new TaskPlanningException(
                    $"Planning agent execution failed: {result.Error}");
            }

            try
            {
                var plan = ParseAndValidate(request.ProjectId, result.Output);

                _logger.LogInformation(
                    "Task plan created for project {ProjectId} on attempt {Attempt}/{MaxAttempts} with {TaskCount} task(s)",
                    request.ProjectId, attempt, _options.MaxAttempts, plan.Tasks.Count);

                return plan;
            }
            catch (Exception ex) when (ex is TaskPlanningException or InvalidTaskPlanException)
            {
                lastFailure = ex;
                previousError = ex.Message;

                _logger.LogWarning(
                    "Task plan attempt {Attempt}/{MaxAttempts} for project {ProjectId} was invalid: {Error}",
                    attempt, _options.MaxAttempts, request.ProjectId, ex.Message);
            }
        }

        throw new TaskPlanningException(
            $"Planning failed after {_options.MaxAttempts} attempt(s) for project '{request.ProjectId}'.",
            lastFailure);
    }

    private async Task<string> BuildContextAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var categories = new[] { ContextCategory.ProjectRequirements, ContextCategory.ArchitectureDocuments };
        var sections = new List<string>();

        foreach (var category in categories)
        {
            var result = await _contextProvider.QueryAsync(
                new ContextQuery(projectId, category, ContextDocumentsPerCategory), cancellationToken);

            foreach (var document in result.Documents)
            {
                sections.Add($"[{category}] {document.Source}\n{document.Content}");
            }
        }

        return string.Join("\n\n", sections);
    }

    private const string PlanJsonSchema =
        """
        {
          "tasks": [
            {
              "id": "TASK-001",
              "title": "...",
              "description": "...",
              "type": "Analysis|Specification|Architecture|Planning|Implementation|Test|Review|Repair|Documentation",
              "priority": "Low|Normal|High|Critical",
              "dependencies": ["TASK-000"],
              "acceptanceCriteria": "...",
              "estimatedComplexity": "Simple|Standard|Complex|Critical"
            }
          ]
        }
        """;

    private static string BuildPrompt(TaskPlanRequest request, string contextText, string? previousError)
    {
        var retryNotice = previousError is null
            ? string.Empty
            : $"\n\nYour previous response was invalid: {previousError}\nFix it and respond again with ONLY valid JSON matching the schema below.";

        return
            $"""
            You are a software project planner. Transform the following approved
            specification into an executable task graph.

            Specification: {request.SpecificationTitle}
            {request.SpecificationContent}

            Context:
            {contextText}

            Respond with ONLY a JSON object of this exact shape (no prose, no markdown fences):
            {PlanJsonSchema}

            Every "id" must be unique. Every "dependencies" entry must reference
            an "id" that also appears in this same plan. The plan must contain
            at least one task with no dependencies and at least one task that
            nothing else depends on.{retryNotice}
            """;
    }

    private static TaskPlan ParseAndValidate(Guid projectId, string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new TaskPlanningException("Planner returned an empty response.");
        }

        TaskPlanDto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<TaskPlanDto>(StripMarkdownFences(rawOutput), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new TaskPlanningException($"Planner output was not valid JSON: {ex.Message}", ex);
        }

        if (dto is null || dto.Tasks.Count == 0)
        {
            throw new TaskPlanningException("Planner returned no tasks.");
        }

        var idMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var taskDto in dto.Tasks)
        {
            if (string.IsNullOrWhiteSpace(taskDto.Id))
            {
                throw new TaskPlanningException("Planner returned a task with a missing 'id'.");
            }

            if (!idMap.TryAdd(taskDto.Id, Guid.NewGuid()))
            {
                throw new TaskPlanningException($"Planner returned duplicate task id '{taskDto.Id}'.");
            }
        }

        var plannedTasks = new List<PlannedTask>();

        foreach (var taskDto in dto.Tasks)
        {
            if (string.IsNullOrWhiteSpace(taskDto.Title))
            {
                throw new TaskPlanningException($"Task '{taskDto.Id}' is missing a required 'title'.");
            }

            if (!Enum.TryParse<WorkItemType>(taskDto.Type, true, out var type))
            {
                throw new TaskPlanningException(
                    $"Task '{taskDto.Id}' has an invalid 'type': '{taskDto.Type}'.");
            }

            var dependencies = new List<Guid>();

            foreach (var depId in taskDto.Dependencies ?? new List<string>())
            {
                if (!idMap.TryGetValue(depId, out var depGuid))
                {
                    throw new TaskPlanningException(
                        $"Task '{taskDto.Id}' references unknown dependency '{depId}'.");
                }

                dependencies.Add(depGuid);
            }

            var complexity = Enum.TryParse<TaskComplexity>(taskDto.EstimatedComplexity, true, out var parsedComplexity)
                ? parsedComplexity
                : TaskComplexity.Standard;

            plannedTasks.Add(new PlannedTask(
                idMap[taskDto.Id],
                taskDto.Title,
                taskDto.Description ?? string.Empty,
                type,
                string.IsNullOrWhiteSpace(taskDto.Priority) ? "Normal" : taskDto.Priority,
                dependencies,
                taskDto.AcceptanceCriteria,
                complexity));
        }

        var plan = new TaskPlan(projectId, plannedTasks);

        // Structural validation is not re-implemented here — reuses the
        // existing, already-tested TaskPlanValidator (duplicate IDs,
        // missing dependency references, cycles, root/terminal checks).
        TaskPlanValidator.Validate(plan);

        return plan;
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');

        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        if (lastFence >= 0)
        {
            trimmed = trimmed[..lastFence];
        }

        return trimmed.Trim();
    }

    private sealed class TaskPlanDto
    {
        public List<PlannedTaskDto> Tasks { get; set; } = new();
    }

    private sealed class PlannedTaskDto
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string Type { get; set; } = string.Empty;

        public string? Priority { get; set; }

        public List<string>? Dependencies { get; set; }

        public string? AcceptanceCriteria { get; set; }

        public string? EstimatedComplexity { get; set; }
    }
}
