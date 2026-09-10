using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Providers;

/// <summary>
/// Decorates an <see cref="IModelProvider"/> with centralized resilience
/// (the <see cref="ResilienceOptions.ProviderCall"/> profile: retry, circuit
/// breaker, bulkhead, timeout) — mirrors <c>ResilienceAgentExecutorDecorator</c>.
/// Retries only normalized, fallback-eligible failures (RateLimited, Timeout,
/// Unavailable, ProviderFailure); Authentication/InvalidRequest/Cancelled
/// pass straight through untouched. Never lets a provider-specific or
/// resilience-internal exception escape — always returns a normalized
/// <see cref="AiCompletionResult"/>.
/// </summary>
public sealed class ResilienceModelProviderDecorator : IModelProvider
{
    private static readonly HashSet<AiFailureCategory> RetryableCategories = new()
    {
        AiFailureCategory.RateLimited,
        AiFailureCategory.Timeout,
        AiFailureCategory.Unavailable,
        AiFailureCategory.ProviderFailure
    };

    private readonly IModelProvider _inner;
    private readonly IResilienceExecutor _resilienceExecutor;
    private readonly ILogger<ResilienceModelProviderDecorator> _logger;

    public ResilienceModelProviderDecorator(
        IModelProvider inner,
        IResilienceExecutor resilienceExecutor,
        ILogger<ResilienceModelProviderDecorator> logger)
    {
        _inner = inner;
        _resilienceExecutor = resilienceExecutor;
        _logger = logger;
    }

    public string ProviderId => _inner.ProviderId;

    public IReadOnlyCollection<string> SupportedCapabilities => _inner.SupportedCapabilities;

    public async Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _resilienceExecutor.ExecuteAsync(
                ResilienceOptions.ProviderCall,
                async ct =>
                {
                    var result = await _inner.CompleteAsync(request, ct);

                    if (!result.Success && result.FailureCategory.HasValue
                        && RetryableCategories.Contains(result.FailureCategory.Value))
                    {
                        throw new AiProviderTransientException(result);
                    }

                    return result;
                },
                cancellationToken);
        }
        catch (AiProviderTransientException ex)
        {
            // Resilience profile disabled or MaxRetryAttempts=0: first (only) attempt's failure surfaces directly.
            return ex.FailureResult;
        }
        catch (RetryExhaustedException ex) when (ex.InnerException is AiProviderTransientException transient)
        {
            _logger.LogWarning(
                "AI provider {ProviderId} retries exhausted for model {ModelId}: {Message}",
                _inner.ProviderId, transient.FailureResult.ModelId, ex.Message);

            return transient.FailureResult with
            {
                ErrorMessage = $"Retries exhausted after {ex.AttemptsPerformed} attempt(s): {transient.FailureResult.ErrorMessage}"
            };
        }
        catch (OperationTimeoutException ex)
        {
            _logger.LogWarning("AI provider {ProviderId} timed out overall: {Message}", _inner.ProviderId, ex.Message);
            return NormalizedFailure(request, AiFailureCategory.Timeout, ex.Message);
        }
        catch (CircuitOpenException ex)
        {
            _logger.LogWarning("AI provider {ProviderId} short-circuited: {Message}", _inner.ProviderId, ex.Message);
            return NormalizedFailure(request, AiFailureCategory.Unavailable, ex.Message);
        }
        catch (BulkheadRejectedException ex)
        {
            _logger.LogWarning("AI provider {ProviderId} bulkhead rejected: {Message}", _inner.ProviderId, ex.Message);
            return NormalizedFailure(request, AiFailureCategory.Unavailable, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NormalizedFailure(request, AiFailureCategory.Cancelled, "Operation was cancelled.");
        }
    }

    private AiCompletionResult NormalizedFailure(AiCompletionRequest request, AiFailureCategory category, string message)
    {
        return new AiCompletionResult(
            false, null, _inner.ProviderId, request.PreferredModelId ?? "unknown", request.CorrelationId, DurationMs: 0,
            FailureCategory: category, ErrorMessage: message);
    }
}
