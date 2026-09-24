namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Execution-adapter migration Phase B — one agent runtime behind a
/// provider-neutral contract. Implementations live in Infrastructure and are selected by
/// <see cref="IExecutionAdapterRegistry"/> through a configured execution
/// profile; nothing in Application or Domain ever names a concrete runtime.
///
/// An adapter executes exactly one <see cref="AgentExecutionSpec"/> and reports
/// an <see cref="ExecutionOutcome"/>. It never owns AgentRun/WorkItem/Project
/// state, never performs acceptance/review/approval, and never loads unrelated
/// data from persistence — everything it needs is in the spec.
/// </summary>
public interface IExecutionAdapter
{
    /// <summary>Stable registry key (e.g. the configured profile's adapter name). Compared case-insensitively.</summary>
    string Key { get; }

    ExecutionCapabilities Capabilities { get; }

    /// <summary>
    /// Exceptions are not translated by the dispatcher: an adapter that throws
    /// propagates to the orchestrator exactly as the runtime it wraps would have.
    /// </summary>
    Task<ExecutionOutcome> ExecuteAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Only the capabilities a current requirement actually checks. Extended when a
/// future adapter introduces a real requirement (cancellation, streaming, usage),
/// never speculatively.
/// </summary>
[Flags]
public enum ExecutionCapabilities
{
    None = 0,

    /// <summary>
    /// The adapter honours a Factory-routed <see cref="ModelRoute.ModelId"/>. A
    /// spec that carries a model id may only be dispatched to an adapter that
    /// declares this — otherwise the Factory's routing decision would be
    /// silently ignored.
    /// </summary>
    FactoryRoutedModel = 1
}
