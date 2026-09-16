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
                "PlannerRuntimeFailed AgentRunId={AgentRunId} Status={Status}", run.Id, run.Status);
            return new PlannerMaterializationResult(
                PlannerMaterializationOutcome.RuntimeFailed, null,
                run.ErrorMessage ?? $"Planner AgentRun ended in status '{run.Status}'.");
        }

        if (run.ProjectId is not { } projectId)
        {
            return PlannerMaterializationResult.Skip($"Planner AgentRun '{agentRunId}' has no ProjectId.");
        }

        var structuredResult = TryDeserializeStructuredResult(run.StructuredResult);

        if (structuredResult is null || structuredResult.Status == StructuredResultStatus.Invalid)
        {
            _logger.LogWarning(
                "PlannerCandidateInvalid AgentRunId={AgentRunId} Reason=NoValidStructuredResult", run.Id);
            return new PlannerMaterializationResult(
                PlannerMaterializationOutcome.CandidateInvalid, null,
                "Planner AgentRun succeeded but produced no valid structured result.");
        }

        ProjectPlanRequest candidate;

        try
        {
            candidate = _resultMapper.Map(projectId, structuredResult);
        }
        catch (PlannerCandidatePlanException ex)
        {
            _logger.LogWarning(
                "PlannerCandidateInvalid AgentRunId={AgentRunId} Reason={Reason}", run.Id, ex.Message);
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
                "PlannerPlanValidationFailed AgentRunId={AgentRunId} Reason={Reason}", run.Id, ex.Message);
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

        var criterionTypes = string.Join(", ", Enum.GetNames<AcceptanceCriterionType>());

        var jsonShape =
            """
            {"plan":{"version":1,"summary":"<one-line plan summary>","tasks":[
              {"planKey":"<stable.dotted.key>","title":"<task title>","description":"<task description>",
                "type":"<one of: WORK_ITEM_TYPES>","priority":"<one of: PRIORITIES>",
                "dependsOn":["<planKey of a prerequisite task>", "..."],
                "acceptanceCriteria":{"version":1,"criteria":[
                  {"type":"<one of: CRITERION_TYPES>","path":"<repository-relative file path>","value":"<expected text>"}
                ]}}
            ]}}
            """
            .Replace("WORK_ITEM_TYPES", workItemTypes)
            .Replace("PRIORITIES", priorities)
            .Replace("CRITERION_TYPES", criterionTypes);

        return $"""
            You are the Planner Agent for AI Factory. Produce a candidate software
            execution plan for the project described below. Your output is
            UNTRUSTED input to the Factory: it will be independently validated,
            normalized, and persisted — you do not create WorkItems yourself.

            Project name: {project.Name}
            Project description: {project.Description}

            Respond using the standard structured-JSON output contract (see the
            Output contract section below), with your candidate plan placed under
            the top-level "data" object as:

            {jsonShape}

            Rules:
            - Every task needs a short, stable, human-readable "planKey" (e.g.
              "api.authentication") — never a random id or GUID.
            - "dependsOn" entries must be "planKey" values of other tasks in this
              same plan, describing real prerequisite relationships — never invent
              a dependency that is not actually required.
            - Use only the listed "type" and "priority" values above.
            - EVERY task must carry "acceptanceCriteria" stating observable
              evidence that the task is done. The Factory verifies these itself
              against the real repository — a task whose completion cannot be
              observed will be rejected and the whole plan refused.
            - Use only the listed criterion types above. "path" is always a
              repository-relative file path. "value" is the exact expected text
              and is required for ExpectedTextPresent, ExpectedTextAbsent and
              ExpectedFileContent; BuildPasses and TestsPass take neither.
            - Prefer criteria that cannot be satisfied by accident: name the
              specific file the task changes and the specific text it must (or
              must no longer) contain.
            - Never put a shell command, script, regular expression or
              natural-language assertion in a criterion — only the listed types.
            - Do not write implementation code.
            - Do not include any prose outside the single structured JSON object.
            - Do not attempt to execute tools, modify files, run shell commands, or
              perform any Git operation — your only job is to produce the plan.
            """;
    }
}
