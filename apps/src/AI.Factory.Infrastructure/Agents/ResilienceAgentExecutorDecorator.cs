using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Decorates an IAgentExecutor with centralized resilience policies (AgentExecution profile).
/// Retries transient process startup or infrastructure failures with exponential backoff and jitter,
/// while passing deterministic agent failures straight through without retrying.
/// </summary>
public sealed class ResilienceAgentExecutorDecorator : IAgentExecutor
{
    private readonly IAgentExecutor _inner;
    private readonly IResilienceExecutor _resilienceExecutor;
    private readonly IFailureClassifier _failureClassifier;
    private readonly ILogger<ResilienceAgentExecutorDecorator> _logger;

    public ResilienceAgentExecutorDecorator(
        IAgentExecutor inner,
        IResilienceExecutor resilienceExecutor,
        IFailureClassifier failureClassifier,
        ILogger<ResilienceAgentExecutorDecorator> logger)
    {
        _inner = inner;
        _resilienceExecutor = resilienceExecutor;
        _failureClassifier = failureClassifier;
        _logger = logger;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _resilienceExecutor.ExecuteAsync(
                ResilienceOptions.AgentExecution,
                async ct =>
                {
                    var result = await _inner.ExecuteAsync(request, ct);

                    // Phase D.1: Only retry genuine transient launch failures (e.g. temporary daemon unavailable).
                    // Timeouts, cancellations, agent non-zero exits, and deterministic Docker configuration failures never retry.
                    if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
                    {
                        if (AgentExecutionFailureClassifier.IsTransientLaunchFailure(result))
                        {
                            throw new IOException($"Transient agent execution error: {result.Error}");
                        }
                    }

                    return result;
                },
                cancellationToken);
        }
        catch (RetryExhaustedException ex) when (ex.InnerException is not null)
        {
            _logger.LogWarning("Agent execution retries exhausted for agent {Agent}: {Message}", request.Agent, ex.Message);
            return new AgentExecutionResult(false, null, ex.Message, 0);
        }
        catch (OperationTimeoutException ex)
        {
            _logger.LogWarning("Agent execution timed out overall for agent {Agent}: {Message}", request.Agent, ex.Message);
            return new AgentExecutionResult(false, null, ex.Message, 0);
        }
        catch (CircuitOpenException ex)
        {
            _logger.LogWarning("Agent execution short-circuited for agent {Agent}: {Message}", request.Agent, ex.Message);
            return new AgentExecutionResult(false, null, ex.Message, 0);
        }
        catch (BulkheadRejectedException ex)
        {
            _logger.LogWarning("Agent execution bulkhead rejected for agent {Agent}: {Message}", request.Agent, ex.Message);
            return new AgentExecutionResult(false, null, ex.Message, 0);
        }
    }
}
