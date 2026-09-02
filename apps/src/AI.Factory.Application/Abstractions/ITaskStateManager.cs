namespace AI.Factory.Application.Abstractions;

public interface ITaskStateManager
{
    /// <summary>
    /// Atomically marks the WorkItem as Running if it is not already Running.
    /// Returns false if it was already Running (prevents double execution).
    /// </summary>
    Task<bool> TryStartAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failure. If attempts remain (AttemptCount &lt; MaxAttempts),
    /// the item is reset to a retryable state; otherwise it is marked Failed.
    /// Returns true if attempts were exhausted (transitioned to Failed),
    /// false if it was reset for another retry (transitioned to Repairing).
    /// </summary>
    Task<bool> FailAsync(
        Guid workItemId,
        string error,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds every WorkItem left in the Running state (orphaned by a process
    /// crash/restart — nothing could still genuinely be executing them) and
    /// applies the same attempts-remaining-vs-exhausted transition as
    /// <see cref="FailAsync"/>, so the scheduler can pick them up again
    /// instead of deadlocking forever. Call once at process startup.
    /// </summary>
    Task RecoverOrphanedTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records proof of liveness for a Running WorkItem plus, when
    /// non-null, the label of the last successfully completed pipeline
    /// stage — never touches Status/AttemptCount, purely observational.
    /// </summary>
    Task HeartbeatAsync(
        Guid workItemId,
        string? checkpoint = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlike RecoverOrphanedTasksAsync (which assumes a full-process
    /// restart and sweeps every Running item unconditionally), this is safe
    /// to call periodically from a long-lived process: it only recovers a
    /// Running item whose LastHeartbeatAtUtc (or StartedAtUtc, if no
    /// heartbeat was ever recorded) is older than the configured stale
    /// timeout — a genuinely still-executing item is left untouched.
    /// Idempotent: an item already moved out of Running by a previous call
    /// no longer matches the query, so calling this repeatedly is a no-op
    /// once nothing is stale. Returns the number of WorkItems recovered.
    /// </summary>
    Task<int> RecoverStaleExecutionsAsync(CancellationToken cancellationToken = default);
}
