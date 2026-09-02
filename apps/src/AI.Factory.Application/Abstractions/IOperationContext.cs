namespace AI.Factory.Application.Abstractions;

/// <summary>
/// One correlated unit of work — an HTTP request, a background orchestration
/// run, or a nested step within either. Immutable snapshot; a new instance
/// is pushed/popped by IOperationScopeFactory as scopes begin/end.
/// </summary>
public sealed record OperationContext(
    Guid CorrelationId,
    Guid OperationId,
    Guid? ParentOperationId,
    string OperationName,
    Guid? ProjectId,
    Guid? TaskId,
    Guid? RunId,
    DateTime StartedAtUtc);

/// <summary>
/// Ambient accessor for the current OperationContext — deliberately free of
/// any ASP.NET/EF dependency so it works identically for API requests and
/// Worker/background execution. Only IOperationScopeFactory mutates it.
/// </summary>
public interface IOperationContextAccessor
{
    OperationContext? Current { get; }
}

/// <summary>AsyncLocal-based implementation — flows correctly across async/await, isolated per logical call chain.</summary>
public sealed class OperationContextAccessor : IOperationContextAccessor
{
    private static readonly AsyncLocal<OperationContext?> Holder = new();

    public OperationContext? Current => Holder.Value;

    internal void Set(OperationContext? value) => Holder.Value = value;
}
