using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IOrchestrationRunRepository
{
    Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default);

    /// <summary>Includes Attempts, ProgressEvents and Steps — the full detail the status/attempts/logs/trace endpoints need.</summary>
    Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>The most recent non-terminal run for a WorkItem — how IOrchestrationProgressReporter finds "the run currently driving this WorkItem" without the orchestrator needing to know about runs at all.</summary>
    Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims exactly one Pending run (Pending → Claimed via a
    /// single conditional UPDATE, not read-then-write) so two Worker
    /// processes can poll concurrently without ever claiming the same run
    /// twice. Returns null when nothing is Pending.
    /// </summary>
    Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default);

    Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default);

    Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default);

    /// <summary>Assigns Sequence (max existing Sequence for the run + 1) and persists a new step.</summary>
    Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default);

    /// <summary>The most recently started step of this Stage/AttemptNumber still Running, for a given run — used to complete/fail the step a matching StartStepAsync opened.</summary>
    Task<OrchestrationRunStep?> FindLatestOpenStepAsync(
        Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default);

    Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default);

    Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Renews WorkerHeartbeatAtUtc/LeaseExpiresAtUtc for a run currently being executed — see RecoverStaleLeasesAsync.</summary>
    Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds every Claimed/Running run whose lease has expired (the Worker
    /// that claimed it died mid-execution) and returns it to Pending if
    /// attempts remain, or Failed(InfrastructureFailure) if exhausted —
    /// never blindly reruns; the same attempt-count check FailAsync/
    /// RecoverOrphanedTasksAsync already use elsewhere in this codebase.
    /// </summary>
    Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a claimed/running run's lease and reverts its status to Pending upon graceful shutdown,
    /// allowing immediate resumption upon restart without waiting for lease expiration.
    /// </summary>
    Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default);
}
