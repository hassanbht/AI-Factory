using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.3 — bridges the existing readiness infrastructure
/// (ITaskDependencyResolver, reused unmodified) to controlled dispatch: for
/// one project, finds Ready WorkItems, orders them deterministically, claims
/// up to the configured concurrency headroom via the existing
/// optimistic-concurrency Version token (no new locking), and creates a
/// Queued AgentRun per claimed WorkItem for the existing AgentRunWorker/
/// AgentOrchestrator/Hermes pipeline to execute. Deliberately independent of
/// the legacy ITaskScheduler/ITaskStateManager/AutonomousCodingOrchestrator
/// pipeline (HermesExecution-based) — this dispatches into the AgentRun
/// pipeline instead, per Phase 18.3 §1/§6.
/// </summary>
public sealed class TaskDispatchScheduler : ITaskDispatchScheduler
{
    private static readonly IReadOnlyDictionary<string, int> PriorityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Critical"] = 0,
            ["High"] = 1,
            ["Normal"] = 2,
            ["Low"] = 3
        };

    private readonly IWorkItemRepository _workItems;
    private readonly IProjectRepository _projects;
    private readonly ITaskDependencyResolver _resolver;
    private readonly IAgentRunRepository _agentRuns;
    private readonly IAgentDefinitionRepository _agentDefinitions;
    private readonly TaskSchedulerOptions _options;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<TaskDispatchScheduler> _logger;

    public TaskDispatchScheduler(
        IWorkItemRepository workItems,
        IProjectRepository projects,
        ITaskDependencyResolver resolver,
        IAgentRunRepository agentRuns,
        IAgentDefinitionRepository agentDefinitions,
        TaskSchedulerOptions options,
        IOperationalMetrics metrics,
        ILogger<TaskDispatchScheduler> logger)
    {
        _workItems = workItems;
        _projects = projects;
        _resolver = resolver;
        _agentRuns = agentRuns;
        _agentDefinitions = agentDefinitions;
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<SchedulingCycleResult> RunCycleAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        _metrics.RecordSchedulingCycleRun();

        var items = await _workItems.GetByProjectIdAsync(projectId, cancellationToken);

        IReadOnlyDictionary<Guid, TaskReadinessStatus> readiness;

        try
        {
            readiness = _resolver.Resolve(items);
        }
        catch (CircularWorkItemDependencyException ex)
        {
            // Cycles are rejected at write time by TaskDependencyService —
            // reaching one here means data changed out from under this
            // read. Isolate it to this cycle rather than throwing the whole
            // scheduler down (Phase 18.3 §13).
            _logger.LogError(ex, "SchedulerCycleFailed ProjectId={ProjectId} Reason=CircularDependency", projectId);
            _metrics.RecordSchedulerDispatchFailure();
            return new SchedulingCycleResult(
                projectId, startedAtUtc, DateTime.UtcNow, 0, 0, Array.Empty<TaskDispatchOutcome>());
        }

        var runningCount = items.Count(x => x.Status == WorkItemStatus.Running.ToString());
        var capacity = Math.Max(0, _options.MaxConcurrentTasks - runningCount);

        // TaskReadinessStatus.Ready only measures dependency satisfaction —
        // ITaskDependencyResolver.Classify treats any non-terminal,
        // non-Running own status (including Draft) as a candidate. The
        // scheduler additionally requires the WorkItem's own lifecycle
        // status to already be WorkItemStatus.Ready, since the only valid
        // domain transition it is allowed to make is Ready -> Running
        // (WorkItemTransitions; Phase 18.3 §9) — Draft must first be moved
        // to Ready through the existing task-status API/workflow.
        var readyOrdered = items
            .Where(x => readiness[x.Id] == TaskReadinessStatus.Ready
                && x.Status == WorkItemStatus.Ready.ToString())
            .OrderBy(x => GetPriorityRank(x.Priority))
            .ThenBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToList();

        var candidates = readyOrdered.Take(capacity).ToList();
        var skippedDueToConcurrency = readyOrdered.Count - candidates.Count;

        if (skippedDueToConcurrency > 0)
        {
            _metrics.RecordSchedulerConcurrencyLimitSkipped(skippedDueToConcurrency);
        }

        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);
        var project = candidates.Count > 0 ? await _projects.GetByIdAsync(projectId, cancellationToken) : null;
        var outcomes = new List<TaskDispatchOutcome>();

        foreach (var workItem in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                outcomes.Add(await DispatchOneAsync(workItem, definitions, project, items, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One WorkItem's dispatch failure must not stop the cycle —
                // Phase 18.3 §13.
                _logger.LogError(ex, "SchedulerDispatchFailed WorkItemId={WorkItemId}", workItem.Id);
                _metrics.RecordSchedulerDispatchFailure();
                outcomes.Add(new TaskDispatchOutcome(
                    workItem.Id, false, null, TaskDispatchSkipReason.DispatchFailed, ex.Message));
            }
        }

        return new SchedulingCycleResult(
            projectId, startedAtUtc, DateTime.UtcNow, readyOrdered.Count, skippedDueToConcurrency, outcomes);
    }

    private async Task<TaskDispatchOutcome> DispatchOneAsync(
        WorkItem workItem,
        IReadOnlyList<AgentDefinition> definitions,
        Project? project,
        IReadOnlyList<WorkItem> allProjectItems,
        CancellationToken cancellationToken)
    {
        // Looked up by convention (AgentType == WorkItem.Type name) — no
        // Planner/Coder-specific agent is created or assumed here; if the
        // operator never registered a matching AgentDefinition, the task is
        // simply not dispatchable yet (Phase 18.3 §20: scheduler stays generic).
        var definition = definitions.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.AgentType, workItem.Type.ToString(), StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            _logger.LogWarning(
                "SchedulerSkipped WorkItemId={WorkItemId} Reason=NoAgentDefinition Type={Type}",
                workItem.Id, workItem.Type);
            return new TaskDispatchOutcome(
                workItem.Id, false, null, TaskDispatchSkipReason.NoAgentDefinition,
                $"No enabled AgentDefinition with AgentType '{workItem.Type}'.");
        }

        // Atomic claim: the domain transition plus the existing optimistic-
        // concurrency Version token (FactoryDbContext.SaveChangesAsync
        // converts a lost race into ConcurrencyConflictException) is the
        // race guard — no new locking mechanism, works across processes
        // because it's enforced at the database row level.
        workItem.TransitionTo(WorkItemStatus.Running);

        try
        {
            await _workItems.UpdateAsync(workItem, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _metrics.RecordSchedulerRaceLost();
            _logger.LogInformation("SchedulerClaimLost WorkItemId={WorkItemId}", workItem.Id);
            return new TaskDispatchOutcome(
                workItem.Id, false, null, TaskDispatchSkipReason.ConcurrentClaimLost,
                "Another scheduler cycle claimed this WorkItem first.");
        }

        var run = new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = workItem.ProjectId,
            WorkItemId = workItem.Id,
            Input = BuildObjective(workItem, project, allProjectItems)
        };

        // CreateAsync is idempotent on CorrelationId (a fresh Guid here, so
        // always a genuinely new row) — mirrors AgentRunService.CreateAsync's
        // Created -> Queued handoff so AgentRunWorker.TryClaimNextPendingAsync
        // picks it up.
        run = await _agentRuns.CreateAsync(run, cancellationToken);

        if (run.Status == AgentRunStatus.Created)
        {
            run.TransitionTo(AgentRunStatus.Queued);
            await _agentRuns.UpdateAsync(run, cancellationToken);
        }

        _metrics.RecordSchedulerTaskDispatched();
        _logger.LogInformation(
            "SchedulerDispatched WorkItemId={WorkItemId} AgentRunId={AgentRunId} AgentDefinitionId={AgentDefinitionId}",
            workItem.Id, run.Id, definition.Id);

        return new TaskDispatchOutcome(workItem.Id, true, run.Id, null, null);
    }

    /// <summary>
    /// Phase 18.7 §8 — the WorkItem-execution Objective. AgentOrchestrator
    /// already routes every AgentRun's Input through the existing
    /// IAgentInstructionBuilder as its "Objective" (unchanged, no new prompt
    /// framework here — same approach as Phase 18.6's Planner Objective), so
    /// enriching this text is the only change needed to give the agent
    /// enough context and an explicit execution-result contract.
    /// </summary>
    private static string BuildObjective(WorkItem workItem, Project? project, IReadOnlyList<WorkItem> allProjectItems)
    {
        var dependencyContext = workItem.Dependencies.Count == 0
            ? "None."
            : string.Join("\n", workItem.Dependencies
                .Select(d => allProjectItems.FirstOrDefault(x => x.Id == d.DependsOnWorkItemId))
                .Where(x => x is not null)
                .Select(x => $"- {x!.Title} (status: {x.Status})"));

        const string executionShape =
            """{"execution":{"version":1,"status":"completed|repair_required","summary":"<one-line summary>","artifacts":[],"tests":[],"notes":[]}}""";

        return $"""
            You are executing one WorkItem for AI Factory. Work only on the
            WorkItem described below — do not silently expand scope to other
            files, features, or WorkItems.

            Project name: {project?.Name}
            Project description: {project?.Description}

            WorkItem PlanKey: {workItem.PlanKey ?? "(none)"}
            WorkItem type: {workItem.Type}
            WorkItem priority: {workItem.Priority}
            WorkItem title: {workItem.Title}
            WorkItem description: {workItem.Description}

            Dependency context (already-completed prerequisite tasks):
            {dependencyContext}

            Respond using the standard structured-JSON output contract (see the
            Output contract section below), with your execution result placed
            under the top-level "data" object as:

            {executionShape}

            Rules:
            - "status" must be "completed" only if you actually finished the
              work described above; use "repair_required" if you attempted it
              but could not complete it.
            - Report only tests you actually executed — never fabricate test
              results.
            - Report only what you actually changed.
            - Do not claim "completed" merely because code was generated; it
              must reflect genuine completion of the assigned WorkItem.
            """;
    }

    private static int GetPriorityRank(string priority) =>
        PriorityRank.TryGetValue(priority, out var rank) ? rank : PriorityRank["Normal"];
}
