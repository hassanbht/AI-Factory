using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Decorates the real IAgentExecutor with circuit-breaker protection —
/// preserves the interface boundary (Application still depends only on
/// IAgentExecutor) rather than baking circuit logic into HermesAgentExecutor
/// itself. When the circuit is open, this returns the same controlled
/// AgentExecutionResult(Success=false, ...) shape HermesAgentExecutor
/// already uses for expected failures, so the rest of the pipeline
/// (AutonomousCodingOrchestrator's existing `if (!result.Success)` path)
/// needs no changes to handle a fail-fast provider outage.
/// </summary>
public sealed class CircuitBreakerAgentExecutorDecorator : IAgentExecutor
{
    private readonly IAgentExecutor _inner;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly ILogger<CircuitBreakerAgentExecutorDecorator> _logger;

    public CircuitBreakerAgentExecutorDecorator(
        IAgentExecutor inner, CircuitBreaker circuitBreaker, ILogger<CircuitBreakerAgentExecutorDecorator> logger)
    {
        _inner = inner;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _circuitBreaker.ExecuteAsync(
                () => _inner.ExecuteAsync(request, cancellationToken),
                isFailure: result => !result.Success);
        }
        catch (CircuitOpenException ex)
        {
            _logger.LogWarning("Agent execution short-circuited for agent {Agent}: {Reason}", request.Agent, ex.Message);

            return new AgentExecutionResult(false, null, ex.Message, 0);
        }
    }
}
