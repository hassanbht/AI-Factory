using System.Collections.Concurrent;
using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Shutdown;

/// <summary>
/// Thread-safe in-memory tracker for active operations. Coordinates async draining during shutdown.
/// </summary>
public sealed class BackgroundOperationTracker : IBackgroundOperationTracker
{
    private readonly ConcurrentDictionary<Guid, ActiveOperationInfo> _activeOperations = new();
    private readonly object _lock = new();
    private readonly ILogger<BackgroundOperationTracker>? _logger;
    private TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BackgroundOperationTracker(ILogger<BackgroundOperationTracker>? logger = null)
    {
        _logger = logger;
    }

    public int ActiveCount => _activeOperations.Count;

    public IReadOnlyList<ActiveOperationInfo> GetActiveOperations() =>
        _activeOperations.Values.ToList();

    public IDisposable Track(OperationContext context)
    {
        var info = new ActiveOperationInfo(
            context.OperationId,
            context.CorrelationId,
            context.OperationName,
            context.ProjectId,
            context.TaskId,
            context.RunId,
            context.StartedAtUtc);

        _activeOperations.TryAdd(context.OperationId, info);

        return new OperationTrackingHandle(this, context.OperationId);
    }

    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task drainTask;

        lock (_lock)
        {
            if (_activeOperations.IsEmpty)
            {
                return true;
            }

            drainTask = _drainTcs.Task;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            await drainTask.WaitAsync(linkedCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout fired.
            _logger?.LogWarning(
                "Graceful drain timed out after {TimeoutMs}ms with {ActiveCount} active operation(s) remaining.",
                timeout.TotalMilliseconds, _activeOperations.Count);
            return false;
        }
    }

    private void Untrack(Guid operationId)
    {
        if (!_activeOperations.TryRemove(operationId, out _))
        {
            return;
        }

        lock (_lock)
        {
            if (_activeOperations.IsEmpty)
            {
                _drainTcs.TrySetResult();
                _drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private sealed class OperationTrackingHandle : IDisposable
    {
        private readonly BackgroundOperationTracker _tracker;
        private readonly Guid _operationId;
        private bool _disposed;

        public OperationTrackingHandle(BackgroundOperationTracker tracker, Guid operationId)
        {
            _tracker = tracker;
            _operationId = operationId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _tracker.Untrack(_operationId);
        }
    }
}
