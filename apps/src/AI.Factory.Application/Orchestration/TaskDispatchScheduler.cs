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

    /// <summary>Phase 18.9 §5 — the canonical AgentType a repair AgentDefinition must be registered under.</summary>
    public const string RepairAgentType = "Repair";

    private readonly IWorkItemRepository _workItems;
    private readonly IProjectRepository _projects;
    private readonly IProjectRepository _projects;
    private readonly ITaskDependencyResolver _resolver;
    private readonly IAgentRunRepository _agentRuns;
    private readonly IAgentDefinitionRepository _agentDefinitions;
    private readonly TaskSchedulerOptions _options;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly IProjectCompletionGate _completionGate;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<TaskDispatchScheduler> _logger;

    public TaskDispatchScheduler(
        IWorkItemRepository workItems,
        IProjectRepository projects,
        IProjectRepository projects,
        ITaskDependencyResolver resolver,
        IAgentRunRepository agentRuns,
        IAgentDefinitionRepository agentDefinitions,
        TaskSchedulerOptions options,
        ExecutionLimitsOptions executionLimits,
        IProjectCompletionGate completionGate,
        IOperationalMetrics metrics,
        ILogger<TaskDispatchScheduler> logger)
    {
        _workItems = workItems;
        _projects = projects;
        _projects = projects;
        _resolver = resolver;
        _agentRuns = agentRuns;
        _agentDefinitions = agentDefinitions;
        _options = options;
        _executionLimits = executionLimits;
        _completionGate = completionGate;
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

        // Phase 18.9 §17 — deterministic ordering policy: Repairing
        // candidates are dispatched first (they are already in-flight work
        // closer to completion than an untouched Ready item), then any
        // capacity left over goes to Ready candidates. Repair candidates are
        // independent of ITaskDependencyResolver — a WorkItem only ever
        // reaches Repairing after its dependencies were already satisfied
        // once (Ready -> Running), so re-checking readiness here would be
        // redundant. Capacity is recomputed every cycle, so Ready work is
        // never starved beyond whatever repair work already holds the
        // concurrency budget that cycle.
        var repairOrdered = items
            .Where(x => x.Status == WorkItemStatus.Repairing.ToString())
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToList();

        var repairCandidates = repairOrdered.Take(capacity).ToList();
        var remainingCapacity = capacity - repairCandidates.Count;

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

        var candidates = readyOrdered.Take(remainingCapacity).ToList();
        var skippedDueToConcurrency = readyOrdered.Count - candidates.Count;

        if (skippedDueToConcurrency > 0)
        {
            _metrics.RecordSchedulerConcurrencyLimitSkipped(skippedDueToConcurrency);
        }

        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);
        // Phase 18.10 §18 — always loaded now (not just when there is
        // dispatch work): the Project-completion gate below needs it every
        // cycle, including for a Project already past Implementation with no
        // Ready/Repairing candidates left (e.g. sitting in Testing/Reviewing).
        var project = await _projects.GetByIdAsync(projectId, cancellationToken);
        var outcomes = new List<TaskDispatchOutcome>();

        foreach (var workItem in repairCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                outcomes.Add(await DispatchRepairAsync(workItem, definitions, project, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SchedulerRepairDispatchFailed WorkItemId={WorkItemId}", workItem.Id);
                _metrics.RecordSchedulerDispatchFailure();
                outcomes.Add(new TaskDispatchOutcome(
                    workItem.Id, false, null, TaskDispatchSkipReason.DispatchFailed, ex.Message));
            }
        }

        foreach (var workItem in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                outcomes.Add(await DispatchOneAsync(workItem, definitions, project, items, cancellationToken));
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

        // Phase 18.10 §6/§18 — the smallest deterministic integration point
        // for Project lifecycle progression: reuses the WorkItems already
        // loaded for this cycle, runs after WorkItem dispatch so a
        // just-completed final WorkItem is reflected immediately, and is a
        // no-op for any Project status this gate doesn't own (Planning,
        // HumanReview, terminal, ...). Isolated like every other per-cycle
        // failure mode — one Project's gate evaluation failing must never
        // stop the rest of this cycle's WorkItem-dispatch result from
        // returning.
        if (project is not null)
        {
            try
            {
                await _completionGate.EvaluateAsync(project, items, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ProjectCompletionGateFailed ProjectId={ProjectId}", projectId);
            }
        }

        return new SchedulingCycleResult(
            projectId, startedAtUtc, DateTime.UtcNow, readyOrdered.Count, skippedDueToConcurrency, outcomes);
    }

    public async Task<TaskDispatchOutcome> DispatchWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _workItems.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            return new TaskDispatchOutcome(workItemId, false, null, TaskDispatchSkipReason.WorkItemNotFound,
                $"WorkItem '{workItemId}' was not found.");
        }

        if (workItem.Status != WorkItemStatus.Ready.ToString())
        {
            return new TaskDispatchOutcome(workItemId, false, null, TaskDispatchSkipReason.NotReady,
                $"WorkItem '{workItemId}' is not Ready (current status '{workItem.Status}'); manual execution can only target a Ready WorkItem.");
        }

        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);
        var project = await _projects.GetByIdAsync(workItem.ProjectId, cancellationToken);
        var allProjectItems = await _workItems.GetByProjectIdAsync(workItem.ProjectId, cancellationToken);

        return await DispatchOneAsync(workItem, definitions, project, allProjectItems, cancellationToken);
    }

    private async Task<TaskDispatchOutcome> DispatchOneAsync(
        WorkItem workItem,
        IReadOnlyList<AgentDefinition> definitions,
        Project? project,
        IReadOnlyList<WorkItem> allProjectItems,
        CancellationToken cancellationToken)
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
    /// Phase 18.9 — dispatches a repair AgentRun for a WorkItem currently in
    /// WorkItemStatus.Repairing (the only path into that status is
    /// Testing -> Repairing on a VerificationGate.Failed result, so being
    /// Repairing already IS the proof that a repairable verification failure
    /// occurred — no further check is needed here). Looked up by the
    /// dedicated RepairAgentType convention (not WorkItem.Type, unlike normal
    /// dispatch) so a repair agent is never conflated with the implementation
    /// agent that produced the failing code. Bounded by
    /// ExecutionLimitsOptions.MaxRepairAttempts — the attempt counter is
    /// incremented atomically with the Repairing -> Running claim through the
    /// same optimistic-concurrency Version token DispatchOneAsync uses, so a
    /// duplicate scheduler observation loses the race safely instead of
    /// creating a second repair AgentRun.
    /// </summary>
    private async Task<TaskDispatchOutcome> DispatchRepairAsync(
        WorkItem workItem, IReadOnlyList<AgentDefinition> definitions, Project? project, CancellationToken cancellationToken)
    {
        if (workItem.RepairAttemptCount >= _executionLimits.MaxRepairAttempts)
        {
            var exhaustedMessage =
                $"Maximum repair attempts ({_executionLimits.MaxRepairAttempts}) reached. " +
                $"Latest verification failure: {workItem.CurrentError ?? "(none recorded)"}";

            workItem.CurrentError = exhaustedMessage;

            try
            {
                workItem.TransitionTo(WorkItemStatus.Failed);
                await _workItems.UpdateAsync(workItem, cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                _metrics.RecordSchedulerRaceLost();
                _logger.LogInformation("SchedulerRepairClaimLost WorkItemId={WorkItemId}", workItem.Id);
                return new TaskDispatchOutcome(
                    workItem.Id, false, null, TaskDispatchSkipReason.ConcurrentClaimLost,
                    "Another scheduler cycle claimed this WorkItem first.");
            }

            _logger.LogWarning(
                "SchedulerRepairBudgetExhausted WorkItemId={WorkItemId} RepairAttemptCount={RepairAttemptCount}",
                workItem.Id, workItem.RepairAttemptCount);
            return new TaskDispatchOutcome(
                workItem.Id, false, null, TaskDispatchSkipReason.RepairBudgetExhausted, exhaustedMessage);
        }

        var definition = definitions.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.AgentType, RepairAgentType, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            _logger.LogWarning(
                "SchedulerRepairSkipped WorkItemId={WorkItemId} Reason=NoRepairAgentDefinition", workItem.Id);
            return new TaskDispatchOutcome(
                workItem.Id, false, null, TaskDispatchSkipReason.NoRepairAgentDefinition,
                $"No enabled AgentDefinition with AgentType '{RepairAgentType}'.");
        }

        // Atomic claim: attempt-counter increment + the Repairing -> Running
        // domain transition are written together in one SaveChangesAsync
        // call, guarded by the same Version optimistic-concurrency token as
        // every other WorkItem claim in this scheduler — no new locking.
        workItem.RepairAttemptCount += 1;
        workItem.TransitionTo(WorkItemStatus.Running);

        try
        {
            await _workItems.UpdateAsync(workItem, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _metrics.RecordSchedulerRaceLost();
            _logger.LogInformation("SchedulerRepairClaimLost WorkItemId={WorkItemId}", workItem.Id);
            return new TaskDispatchOutcome(
                workItem.Id, false, null, TaskDispatchSkipReason.ConcurrentClaimLost,
                "Another scheduler cycle claimed this WorkItem first.");
        }

        var run = new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = workItem.ProjectId,
            WorkItemId = workItem.Id,
            Input = BuildRepairObjective(workItem, project)
        };

        run = await _agentRuns.CreateAsync(run, cancellationToken);

        if (run.Status == AgentRunStatus.Created)
        {
            run.TransitionTo(AgentRunStatus.Queued);
            await _agentRuns.UpdateAsync(run, cancellationToken);
        }

        _metrics.RecordSchedulerTaskDispatched();
        _logger.LogInformation(
            "SchedulerRepairDispatched WorkItemId={WorkItemId} AgentRunId={AgentRunId} " +
            "AgentDefinitionId={AgentDefinitionId} RepairAttemptCount={RepairAttemptCount}",
            workItem.Id, run.Id, definition.Id, workItem.RepairAttemptCount);

        return new TaskDispatchOutcome(workItem.Id, true, run.Id, null, null);
    }

    /// <summary>
    /// Phase 18.9 §6 — the repair Objective. Same structured-JSON output
    /// contract as BuildObjective (an "execution.status" of "completed" here
    /// still only means the repair agent finished its work — the WorkItem
    /// still goes through Running -> Testing -> IVerificationGate exactly
    /// like a normal implementation attempt; nothing about this Objective
    /// text can let the agent bypass verification). The verification command
    /// itself is never mentioned — the LLM has no way to supply or influence
    /// it. CurrentError (the bounded VerificationGate.Failed summary) is
    /// truncated defensively again here even though VerificationGate already
    /// bounds it, so this objective can never grow unbounded regardless of
    /// what wrote CurrentError.
    /// </summary>
    private static string BuildRepairObjective(WorkItem workItem, Project? project)
    {
        // Phase 18.21 §12 — measured, not guessed. A repair driven by a short
        // failure summary (an acceptance-criteria miss, ~150 chars) completed
        // in ~84s live; the same model on the same tiny workspace, handed a
        // 2000-char raw `dotnet test` dump, exceeded the 5-minute execution
        // Phase 18.24 — allow sufficient budget for the failing test name,
        // assertion details and line number from VerificationGate without cutting
        // off the decisive signal.
        const int maxFailureSummaryLength = 2000;

        var failureSummary = workItem.CurrentError ?? "(no verification failure summary recorded)";

        if (failureSummary.Length > maxFailureSummaryLength)
        {
            failureSummary = failureSummary[..maxFailureSummaryLength] + " …(truncated)";
        }

        const string executionShape =
            """{"execution":{"version":1,"status":"completed|repair_required","summary":"<one-line summary>","artifacts":[],"tests":[],"notes":[]}}""";

        return $"""
            You are repairing one WorkItem for AI Factory whose previous
            implementation failed independent verification. Your goal is to
            repair the repository so that independent verification succeeds
            and the WorkItem requirements are satisfied. You may modify the
            necessary source and test files to resolve the verification
            failure, but do not make changes unrelated to the reported failure.

            Project name: {project?.Name}
            Project description: {project?.Description}

            WorkItem PlanKey: {workItem.PlanKey ?? "(none)"}
            WorkItem type: {workItem.Type}
            WorkItem title: {workItem.Title}
            WorkItem description: {workItem.Description}

            This is repair attempt {workItem.RepairAttemptCount} of a bounded budget.

            Latest verification failure (the Factory's own deterministic
            build/test run, not a self-report — treat this as ground truth
            for what is currently broken):
            {failureSummary}

            Respond using the standard structured-JSON output contract (see the
            Output contract section below), with your execution result placed
            under the top-level "data" object as:

            {executionShape}

            Rules:
            - "status" must be "completed" only if you actually addressed the
              verification failure above; use "repair_required" if you
              attempted it but could not complete it.
            - Report only tests you actually executed — never fabricate test
              results.
            - Report only what you actually changed.
            - Your success will be independently re-verified by the Factory's
              own deterministic build/test gate — this report alone is never
              sufficient proof that the repair worked, so do not claim
              "completed" merely because code was generated.
            - You may not specify, request, or influence which verification
              command is run; the Factory decides that independently.
            """;
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
