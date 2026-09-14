namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.3 — deterministic dispatch: decides WHEN a WorkItem is eligible
/// and creates the AgentRun that lets the existing AgentRunWorker/
/// AgentOrchestrator/Hermes pipeline execute it. Never calls Hermes/an LLM
/// itself, never duplicates ITaskDependencyResolver's cycle detection.
/// </summary>
public interface ITaskDispatchScheduler
{
    /// <summary>
    /// Runs one scheduling cycle for a single project: discovers Ready
    /// WorkItems, orders them deterministically, claims up to the configured
    /// concurrency headroom, and creates a Queued AgentRun for each claimed
    /// WorkItem. Safe to call repeatedly/concurrently — a lost claim race is
    /// reported as a skip, never a duplicate dispatch.
    /// </summary>
    Task<SchedulingCycleResult> RunCycleAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 18.13 — dispatches exactly one specific WorkItem out-of-cycle,
    /// bypassing readiness ordering and concurrency-headroom capacity (an
    /// explicit manual request targets one named task, not "whatever's next").
    /// The WorkItem must currently be WorkItemStatus.Ready — the same domain
    /// rule every other dispatch path enforces; anything else is reported as a
    /// skip, never forced. Reuses the exact same atomic claim + AgentRun
    /// creation as RunCycleAsync's own dispatch path — no second claim mechanism.
    /// </summary>
    Task<TaskDispatchOutcome> DispatchWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);
}

public enum TaskDispatchSkipReason
{
    ConcurrentClaimLost,
    NoAgentDefinition,
    DispatchFailed,
    NoRepairAgentDefinition,
    RepairBudgetExhausted,
    WorkItemNotFound,
    NotReady
}

public sealed record TaskDispatchOutcome(
    Guid WorkItemId,
    bool Dispatched,
    Guid? AgentRunId,
    TaskDispatchSkipReason? SkipReason,
    string? SkipDetail);

public sealed record SchedulingCycleResult(
    Guid ProjectId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int ReadyCandidateCount,
    int SkippedDueToConcurrencyLimit,
    IReadOnlyList<TaskDispatchOutcome> Outcomes)
{
    public int DispatchedCount => Outcomes.Count(x => x.Dispatched);

    public int FailureCount => Outcomes.Count(x => !x.Dispatched);
}
