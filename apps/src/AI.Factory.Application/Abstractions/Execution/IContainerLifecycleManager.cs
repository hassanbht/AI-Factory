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
    /// Sweeps Factory-owned managed containers that are orphaned (no active AgentRun in the database
    /// or stale beyond the recovery timeout). Never removes unmanaged/foreign containers or
    /// active containers belonging to other healthy executions.
    /// </summary>
    /// <returns>The number of orphaned containers removed.</returns>
    Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default);
}
