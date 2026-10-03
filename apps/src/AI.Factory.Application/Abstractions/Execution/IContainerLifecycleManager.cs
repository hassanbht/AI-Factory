namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Execution Runtime Lifecycle Hardening (Phase D.1).
/// Owns the lifecycle and cleanup of agent execution containers on the underlying host runtime.
/// Ensures that every container is deterministically identifiable, cleanly removed
/// upon completion/failure/timeout/cancellation, and stale orphans are safely swept.
/// </summary>
public interface IContainerLifecycleManager
{
    /// <summary>
    /// Forcibly ensures the container for the specified AgentRun is stopped and removed.
    /// Idempotent: safe to call even if the container was already removed or never started.
    /// </summary>
    Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase O — cleanup after a launch that was interrupted (cancelled, timed out) before the host runtime
    /// necessarily finished creating the container. The runtime may still create it AFTER the launch client was
    /// killed, so a single removal that finds nothing proves nothing: this keeps removing this run's container,
    /// and only this run's, for a short bounded window and finishes with an explicit outcome. Runtimes with no
    /// creation latency (a local process) keep the default, which is the single-shot cleanup.
    /// </summary>
    async Task<ContainerCleanupOutcome> ReconcileCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
    {
        await EnsureCleanupAsync(agentRunId, cancellationToken);
        return ContainerCleanupOutcome.NotApplicable;
    }

    /// <summary>
    /// Sweeps Factory-owned managed containers that are orphaned (no active AgentRun in the database
    /// or stale beyond the recovery timeout). Never removes unmanaged/foreign containers or
    /// active containers belonging to other healthy executions.
    /// </summary>
    /// <returns>The number of orphaned containers removed.</returns>
    Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default);
}

/// <summary>The explicit end state of a runtime cleanup (Phase O).</summary>
public enum ContainerCleanupOutcome
{
    /// <summary>The runtime has no asynchronous creation to reconcile (single-shot cleanup ran).</summary>
    NotApplicable,

    /// <summary>The run's container existed and has been removed.</summary>
    Removed,

    /// <summary>No container of this run was observed for the whole bounded window.</summary>
    NotFound,

    /// <summary>The runtime could not be asked, or refused the removal, within the window.</summary>
    Failed
}
