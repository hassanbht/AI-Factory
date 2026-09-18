using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Planning;

/// <summary>
/// Phase 18.6 — default <see cref="IProjectPlanner"/>. Reuses the existing
/// AgentRun creation path (<see cref="IAgentRunService"/>), the existing
/// AgentRun/AgentDefinition repositories, the existing structured-result
/// persistence on AgentRun, the Phase 18.6 <see cref="IPlannerResultMapper"/>,
/// and the Phase 18.5 <see cref="IProjectPlanService"/> — no second
/// execution queue, no second validation, no second persistence path.
/// </summary>
public sealed class ProjectPlanner : IProjectPlanner
{
    /// <summary>The AgentDefinition.AgentType value that marks an AgentDefinition as usable for Planner execution — distinct from any WorkItemType value (Phase 18.6 §4).</summary>
    public const string PlannerAgentType = "Planner";

    /// <summary>
    /// Phase 18.23 §4/§12 — the task ceiling stated in the Planner prompt. Not
    /// a validation rule (the Factory never rejects a plan for being large);
    /// purely guidance, because every observed malformed live plan was one the
    /// model ran out of budget to finish.
    /// </summary>
    public const int MaxRecommendedTasks = 3;

    private readonly IProjectRepository _projects;
    private readonly IAgentDefinitionRepository _agentDefinitions;
    private readonly IAgentRunRepository _agentRuns;
    private readonly IAgentRunService _agentRunService;
    private readonly IProjectPlanService _planService;
    private readonly IPlannerResultMapper _resultMapper;
    private readonly PlannerOptions _options;
    private readonly ILogger<ProjectPlanner> _logger;

    public ProjectPlanner(
        IProjectRepository projects,
        IAgentDefinitionRepository agentDefinitions,
        IAgentRunRepository agentRuns,
        IAgentRunService agentRunService,
        IProjectPlanService planService,
        IPlannerResultMapper resultMapper,
        PlannerOptions options,
        ILogger<ProjectPlanner> logger)
    {
        _projects = projects;
        _agentDefinitions = agentDefinitions;
        _agentRuns = agentRuns;
        _agentRunService = agentRunService;
        _planService = planService;
        _resultMapper = resultMapper;
        _options = options;
        _logger = logger;
    }

    public async Task<AgentRun> GenerateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

        if (project.Status != ProjectStatus.Planning)
        {
            throw new ConflictException(
                $"Project '{projectId}' must be in '{ProjectStatus.Planning}' status to generate a plan; " +
                $"current status is '{project.Status}'.");
        }

        // §17 — ProjectPlanService remains authoritative; a Project that
        // already has a persisted plan is not silently re-planned.
        var existingPlan = await _planService.GetPlanAsync(projectId, cancellationToken);
        if (existingPlan is not null)
        {
            throw new ConflictException(
                $"Project '{projectId}' already has a persisted execution plan; plan regeneration is not supported in this phase.");
        }

        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);
        var plannerDefinition = definitions.FirstOrDefault(d =>
            d.Enabled && string.Equals(d.AgentType, PlannerAgentType, StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException(
                $"No enabled AgentDefinition with AgentType '{PlannerAgentType}' is registered.");

        // §16 idempotency/concurrency — reuse an already in-flight Planner
        // AgentRun for this Project rather than creating a second one. This
        // is a best-effort de-dup, not a distributed lock: a genuine
        // concurrent race still cannot create duplicate WorkItems, because
        // IProjectPlanService.CreatePlanAsync's own hash-checked idempotency
        // and (ProjectId, PlanKey) unique index are the final guarantee.
        var recentRuns = await _agentRuns.GetPagedAsync(
            new AgentRunQuery(ProjectId: projectId, AgentDefinitionId: plannerDefinition.Id, Page: 1, PageSize: 20),
            cancellationToken);

        var activeRun = recentRuns.Items.FirstOrDefault(r => !AgentRunTransitions.IsTerminal(r.Status));
        if (activeRun is not null)
        {
            _logger.LogInformation(
                "PlannerGenerateReusedActiveRun ProjectId={ProjectId} AgentRunId={AgentRunId} Status={Status}",
                projectId, activeRun.Id, activeRun.Status);
            return activeRun;
        }

        var objective = BuildObjective(project);

        var run = await _agentRunService.CreateAsync(
            new CreateAgentRunCommand(plannerDefinition.Id, projectId, objective, _options.Model), cancellationToken);

        _logger.LogInformation(
            "PlannerGenerateStarted ProjectId={ProjectId} AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId}",
            projectId, run.Id, plannerDefinition.Id);

        return run;
    }

    public async Task<AgentRun?> GetLatestRunAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        (await GetRunsAsync(projectId, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<AgentRun>> GetRunsAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);

        // Deliberately NOT filtered on Enabled: a Planner run that already
        // happened stays a fact about this Project even if the definition was
        // disabled afterwards, and hiding it would let the lifecycle owner
        // dispatch a second Planner for the same Project.
        var plannerDefinitionIds = definitions
            .Where(d => string.Equals(d.AgentType, PlannerAgentType, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Id)
            .ToHashSet();

        if (plannerDefinitionIds.Count == 0)
        {
            return Array.Empty<AgentRun>();
        }

        // GetPagedAsync already returns newest-first (OrderByDescending on
        // CreatedAtUtc), so this list is newest-first too.
        var page = await _agentRuns.GetPagedAsync(
            new AgentRunQuery(ProjectId: projectId, Page: 1, PageSize: 50), cancellationToken);

        return page.Items.Where(r => plannerDefinitionIds.Contains(r.AgentDefinitionId)).ToList();
    }

    public async Task<PlannerMaterializationResult> MaterializeAsync(
        Guid agentRunId, CancellationToken cancellationToken = default)
    {
        var run = await _agentRuns.GetByIdAsync(agentRunId, cancellationToken);

        if (run is null)
        {
            return PlannerMaterializationResult.Skip($"AgentRun '{agentRunId}' was not found.");
        }

        if (run.AgentDefinition is null
            || !string.Equals(run.AgentDefinition.AgentType, PlannerAgentType, StringComparison.OrdinalIgnoreCase))
        {
            return PlannerMaterializationResult.Skip($"AgentRun '{agentRunId}' is not a Planner run.");
        }

        if (!AgentRunTransitions.IsTerminal(run.Status))
        {
            return PlannerMaterializationResult.Skip(
                $"AgentRun '{agentRunId}' has not reached a terminal state yet (Status='{run.Status}').");
        }

        // §14/§15 — LLM/runtime failure is its own category, never
        // misrepresented as (or collapsed into) a plan validation failure.
        if (run.Status != AgentRunStatus.Succeeded)
        {
            _logger.LogInformation(
                "PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Status={Status}",
                PlannerDiagnosticStage.PlannerExecution, run.Id, run.Status);
            return new PlannerMaterializationResult(
                PlannerMaterializationOutcome.RuntimeFailed, null,
                run.ErrorMessage ?? $"Planner AgentRun ended in status '{run.Status}'.");
        }

        if (run.ProjectId is not { } projectId)
        {
            return PlannerMaterializationResult.Skip($"Planner AgentRun '{agentRunId}' has no ProjectId.");
        }

        // §6 — every stage below logs the stage it reached, so a failed Planner
        // run says WHERE the chain broke. What is logged is the Factory's own
        // classification plus the parser's/validator's own message; the raw
        // model output itself is never written to these logs (§6 again) — it
        // stays on AgentRun.Output for deliberate inspection.
        if (string.IsNullOrWhiteSpace(run.Output))
        {
            _logger.LogWarning(
                "PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Reason=NoRawOutput",
                PlannerDiagnosticStage.PlannerRawOutputReceived, run.Id);
            return new PlannerMaterializationResult(
                PlannerMaterializationOutcome.CandidateInvalid, null,
                "Planner AgentRun succeeded but produced no output at all.");
        }

        var structuredResult = TryDeserializeStructuredResult(run.StructuredResult);

        if (structuredResult is null || structuredResult.Status == StructuredResultStatus.Invalid)
        {
            // The parser already recorded WHY, as a warning on the structured
            // result the orchestrator persisted — surface it instead of the
            // uninformative "no valid structured result" this used to report.
            var reason = structuredResult?.Warnings.FirstOrDefault()
                ?? "no structured-result envelope could be read from the agent's output";

            _logger.LogWarning(
                "PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Reason={Reason}",
                PlannerDiagnosticStage.PlannerStructuredParse, run.Id, reason);

            return new PlannerMaterializationResult(
                PlannerMaterializationOutcome.CandidateInvalid, null,
                $"Planner AgentRun succeeded but produced no valid structured result: {reason}");
        }

        ProjectPlanRequest candidate;

        try
        {
            candidate = _resultMapper.Map(projectId, structuredResult);
        }
        catch (PlannerCandidatePlanException ex)
        {
            _logger.LogWarning(
                "PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Reason={Reason}",
                ex.Stage, run.Id, ex.Message);
            return new PlannerMaterializationResult(PlannerMaterializationOutcome.CandidateInvalid, null, ex.Message);
        }

        try
        {
            var plan = await _planService.CreatePlanAsync(candidate, cancellationToken);

            _logger.LogInformation(
                "PlannerPlanMaterialized AgentRunId={AgentRunId} ProjectId={ProjectId} PlanHash={PlanHash} TaskCount={TaskCount}",
                run.Id, projectId, plan.PlanHash, plan.Tasks.Count);

            return new PlannerMaterializationResult(PlannerMaterializationOutcome.Succeeded, plan, null);
        }
        catch (Exception ex) when (ex is ValidationException or ConflictException)
        {
            // §14 — invalid/conflicting candidate never creates partial
            // WorkItems (ProjectPlanService's own transactional persist
            // guarantees that); this is a Factory validation failure, kept
            // distinct from RuntimeFailed/CandidateInvalid above.
            _logger.LogWarning(
                "PlannerStageFailed Stage={Stage} AgentRunId={AgentRunId} Reason={Reason}",
                PlannerDiagnosticStage.PlannerPlanValidation, run.Id, ex.Message);
            return new PlannerMaterializationResult(PlannerMaterializationOutcome.PlanValidationFailed, null, ex.Message);
        }
    }

    private static StructuredAgentResult? TryDeserializeStructuredResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StructuredAgentResult>(json, StructuredAgentResult.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Composes the Objective text handed to AgentOrchestrator's existing
    /// IAgentInstructionBuilder (Phase 18.6 §6). AgentOrchestrator currently
    /// builds every AgentRun's AgentExecutionInstruction with empty
    /// Instructions/Constraints arrays, so this Objective text is the only
    /// channel available today for role-specific guidance — deliberately
    /// not a second prompt-building framework, just a richer Objective.
    /// </summary>
    private static string BuildObjective(Project project)
    {
        var workItemTypes = string.Join(", ", Enum.GetNames<WorkItemType>());
        var priorities = string.Join(", ", ProjectPlanService.ValidPriorities);

        // Every list below is projected from the code that will actually judge
        // the answer (§3/§4) — a criterion type the model is told about is by
        // construction one AcceptanceCriteriaContract accepts, and vice versa.
        var pathOnly = string.Join(", ", new[]
        {
            AcceptanceCriterionType.FileExists, AcceptanceCriterionType.FileAbsent
        });

        var pathAndValue = string.Join(", ", new[]
        {
            AcceptanceCriterionType.ExpectedTextPresent,
            AcceptanceCriterionType.ExpectedTextAbsent,
            AcceptanceCriterionType.ExpectedFileContent
        });

        var neither = string.Join(", ", new[]
        {
            AcceptanceCriterionType.BuildPasses, AcceptanceCriterionType.TestsPass
        });

        var envelopeShape =
            """{"version":1,"status":"Succeeded","summary":"<one line>","output":"","data":{"plan":{"version":1,"summary":"<one line>","tasks":[<task>,...]}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}""";

        var taskShape =
            """{"planKey":"<short.dotted.key>","title":"<short title>","description":"<one sentence>","type":"<type>","priority":"<priority>","dependsOn":[],"acceptanceCriteria":{"version":1,"criteria":[<criterion>,...]}}""";

        var criterionShapes =
            """
            {"type":"FileExists","path":"<repository-relative path>"}
            {"type":"FileAbsent","path":"<repository-relative path>"}
            {"type":"ExpectedTextPresent","path":"<repository-relative path>","value":"<exact text>"}
            {"type":"ExpectedTextAbsent","path":"<repository-relative path>","value":"<exact text>"}
            {"type":"ExpectedFileContent","path":"<repository-relative path>","value":"<exact full file text>"}
            {"type":"BuildPasses"}
            {"type":"TestsPass"}
            """;

        return $"""
            You are the Planner Agent for AI Factory. You are not talking to a
            person: your answer is parsed by a program. Produce ONE JSON object
            and nothing else — no greeting, no explanation, no markdown, no code
            fence.

            Your output is UNTRUSTED input to the Factory: it is independently
            validated and either accepted whole or rejected whole. You do not
            create WorkItems yourself, and a single malformed character rejects
            the entire plan — so keep the JSON small and finish it.

            Project name: {project.Name}
            Project description: {project.Description}

            Emit exactly this envelope, with the plan under "data":

            {envelopeShape}

            Each <task> is exactly:

            {taskShape}

            Each <criterion> is exactly one of:

            {criterionShapes}

            Hard rules:
            - PLAN SIZE: emit the SMALLEST plan that covers the work — at most
              {MaxRecommendedTasks} tasks. One task per concrete change to the
              repository. Never add a task for analysing, reviewing, verifying or
              documenting the plan itself.
            - Never add a task whose job is to build the project, run its tests,
              or check that earlier tasks worked. The Factory already builds and
              tests the repository itself after every single task, and refuses
              the task unless that passes — such a task would repeat work that
              has already happened. If a task's result depends on the build or
              the tests, say so with a {neither} criterion ON THAT TASK instead.
            - "type" is one of: {workItemTypes}.
            - "priority" is one of: {priorities}.
            - "planKey" is short, stable and human-readable (e.g. "docs.readme") —
              never a random id or GUID.
            - "dependsOn" holds "planKey" values of OTHER tasks in this same plan,
              and only where one task genuinely cannot start before another
              finishes. Never invent a dependency that is not actually required,
              and never let a task depend on itself.
            - EVERY task must carry "acceptanceCriteria" with at least one
              criterion stating observable evidence that the task is done. The
              Factory checks these itself against the real repository after the
              work runs — a task whose completion cannot be observed is rejected
              and the whole plan refused.
            - {pathOnly} need "path" only. {pathAndValue} need both "path" and
              "value". {neither} take neither.
            - Never use {neither} as a task's ONLY criterion: they already passed
              before the task started, so they prove nothing about it. Name the
              file the task creates or changes and the exact text it must (or must
              no longer) contain.
            - "path" is always repository-relative — never absolute, never
              outside the repository.
            - Never put a shell command, script, regular expression, glob or
              natural-language sentence in "type", "path" or "value".
            - Keep every "description" to one short sentence. Long prose spends
              the budget the JSON needs in order to stay well-formed.
            - Do not write implementation code. Do not run tools, edit files, run
              shell commands or perform any Git operation — producing the plan is
              your whole job.
            - "status" is "Succeeded" only if the plan under "data" is complete.
            """;
    }
}
