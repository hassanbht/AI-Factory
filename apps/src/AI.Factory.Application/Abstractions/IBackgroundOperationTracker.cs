namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Snapshot of one active in-flight operation.
/// </summary>
public sealed record ActiveOperationInfo(
    Guid OperationId,
    Guid CorrelationId,
    string OperationName,
    Guid? ProjectId,
    Guid? TaskId,
    Guid? RunId,
    DateTime StartedAtUtc);

/// <summary>
/// Tracks in-flight background operations and coordinates graceful draining during shutdown.
/// </summary>
public interface IBackgroundOperationTracker
{
    /// <summary>
    /// Current number of operations in flight.
    /// </summary>
    int ActiveCount { get; }

    /// <summary>
    /// Gets a list of currently active operations.
    /// </summary>
    IReadOnlyList<ActiveOperationInfo> GetActiveOperations();

    /// <summary>
    /// Registers an active operation. Disposing the returned handle completes and unregisters the operation.
    /// </summary>
    IDisposable Track(OperationContext context);

    /// <summary>
    /// Waits until all active operations have drained or the timeout expires.
    /// Returns true if all drained, false if timed out.
    /// </summary>
    Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
