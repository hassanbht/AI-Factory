namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Provider-neutral execution dispatcher that routes an <see cref="AgentExecutionSpec"/>
/// through configured execution profiles to the appropriate <see cref="IExecutionAdapter"/>.
/// </summary>
public interface IAgentExecutionDispatcher
{
    Task<ExecutionOutcome> DispatchAsync(
        AgentExecutionSpec spec,
        CancellationToken cancellationToken = default);
}
