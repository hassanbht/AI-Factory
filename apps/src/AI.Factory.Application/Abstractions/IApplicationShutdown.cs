namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Coordinates application shutdown across API, Worker, and background tasks.
/// Provides state inspection, cancellation tokens for terminating operations,
/// and asynchronous waiting for the host to complete shutdown.
/// </summary>
public interface IApplicationShutdown
{
    /// <summary>
    /// Indicates whether application shutdown has been initiated.
    /// </summary>
    bool IsStopping { get; }

    /// <summary>
    /// Cancellation token triggered when shutdown begins. Propagated to in-flight
    /// and background operations.
    /// </summary>
    CancellationToken StoppingToken { get; }

    /// <summary>
    /// Initiates application shutdown. Returns true if this call transitioned the state
    /// to stopping, or false if shutdown was already initiated.
    /// </summary>
    bool TryBeginShutdown();

    /// <summary>
    /// Asynchronously waits until shutdown has been initiated.
    /// </summary>
    Task WaitForShutdownAsync(CancellationToken cancellationToken = default);
}
