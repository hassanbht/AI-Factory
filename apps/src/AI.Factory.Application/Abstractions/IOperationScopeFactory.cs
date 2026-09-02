using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Begins one traced, correlated operation. Nested calls (a child operation
/// started while a parent is still open) automatically inherit the parent's
/// CorrelationId/ProjectId/TaskId/RunId unless the caller overrides them, and
/// ParentOperationId links back to the parent's OperationId.
/// </summary>
public interface IOperationScopeFactory
{
    IOperationScope Begin(
        string operationName,
        Guid? correlationId = null,
        Guid? projectId = null,
        Guid? taskId = null,
        Guid? runId = null);
}

/// <summary>
/// One open operation. STARTED is logged on Begin(); SUCCEEDED/FAILED plus
/// DurationMs is logged on Dispose — Fail() before disposal marks it FAILED,
/// otherwise it is assumed SUCCEEDED (matches the pattern already used for
/// IOrchestrationRunTraceService: an operation that never explicitly failed
/// completed successfully).
/// </summary>
public interface IOperationScope : IAsyncDisposable
{
    OperationContext Context { get; }

    void Fail(Exception exception);

    void Fail(string reason);
}

public sealed class OperationScopeFactory : IOperationScopeFactory
{
    private readonly OperationContextAccessor _accessor;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<OperationScopeFactory> _logger;

    public OperationScopeFactory(OperationContextAccessor accessor, IOperationalMetrics metrics, ILogger<OperationScopeFactory> logger)
    {
        _accessor = accessor;
        _metrics = metrics;
        _logger = logger;
    }

    public IOperationScope Begin(
        string operationName,
        Guid? correlationId = null,
        Guid? projectId = null,
        Guid? taskId = null,
        Guid? runId = null)
    {
        var parent = _accessor.Current;

        var context = new OperationContext(
            CorrelationId: correlationId ?? parent?.CorrelationId ?? Guid.NewGuid(),
            OperationId: Guid.NewGuid(),
            ParentOperationId: parent?.OperationId,
            OperationName: operationName,
            ProjectId: projectId ?? parent?.ProjectId,
            TaskId: taskId ?? parent?.TaskId,
            RunId: runId ?? parent?.RunId,
            StartedAtUtc: DateTime.UtcNow);

        _accessor.Set(context);

        _logger.LogInformation(
            "Operation STARTED: {OperationName}, CorrelationId={CorrelationId}, OperationId={OperationId}, ParentOperationId={ParentOperationId}, ProjectId={ProjectId}, TaskId={TaskId}, RunId={RunId}",
            context.OperationName, context.CorrelationId, context.OperationId, context.ParentOperationId, context.ProjectId, context.TaskId, context.RunId);

        _metrics.RecordOperationStarted();

        return new OperationScope(this, context, parent);
    }

    private void Restore(OperationContext? parent) => _accessor.Set(parent);

    private sealed class OperationScope : IOperationScope
    {
        private readonly OperationScopeFactory _owner;
        private readonly OperationContext? _parent;
        private readonly DateTime _startedAtUtc;
        private string? _failureReason;
        private bool _disposed;

        public OperationScope(OperationScopeFactory owner, OperationContext context, OperationContext? parent)
        {
            _owner = owner;
            Context = context;
            _parent = parent;
            _startedAtUtc = context.StartedAtUtc;
        }

        public OperationContext Context { get; }

        public void Fail(Exception exception) => _failureReason = exception.Message;

        public void Fail(string reason) => _failureReason = reason;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            var durationMs = (long)(DateTime.UtcNow - _startedAtUtc).TotalMilliseconds;
            var succeeded = _failureReason is null;

            if (succeeded)
            {
                _owner._logger.LogInformation(
                    "Operation SUCCEEDED: {OperationName}, CorrelationId={CorrelationId}, OperationId={OperationId}, DurationMs={DurationMs}",
                    Context.OperationName, Context.CorrelationId, Context.OperationId, durationMs);
            }
            else
            {
                _owner._logger.LogWarning(
                    "Operation FAILED: {OperationName}, CorrelationId={CorrelationId}, OperationId={OperationId}, DurationMs={DurationMs}, Reason={Reason}",
                    Context.OperationName, Context.CorrelationId, Context.OperationId, durationMs, _failureReason);
            }

            _owner._metrics.RecordOperationCompleted(succeeded);
            _owner.Restore(_parent);

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Backward-compatible default for constructors that made IOperationScopeFactory an optional dependency.</summary>
public sealed class NullOperationScopeFactory : IOperationScopeFactory
{
    public static readonly NullOperationScopeFactory Instance = new();

    public IOperationScope Begin(
        string operationName,
        Guid? correlationId = null,
        Guid? projectId = null,
        Guid? taskId = null,
        Guid? runId = null)
    {
        var context = new OperationContext(
            correlationId ?? Guid.NewGuid(), Guid.NewGuid(), null, operationName, projectId, taskId, runId, DateTime.UtcNow);

        return new NullScope(context);
    }

    private sealed class NullScope : IOperationScope
    {
        public NullScope(OperationContext context) => Context = context;

        public OperationContext Context { get; }

        public void Fail(Exception exception) { }

        public void Fail(string reason) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
