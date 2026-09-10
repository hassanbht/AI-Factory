using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Deterministic capability-based AI router. Selection precedence:
/// 1. <see cref="AiCompletionRequest.PreferredModelId"/> if capability-compatible, available and registered.
/// 2. <see cref="AiRoutingOptions.CapabilityPreferredModel"/> entry for the requested capability.
/// 3. Remaining capability-compatible, available candidates ordered by their provider's configured
///    <see cref="ProviderCredentialSettings.Priority"/> (descending), ties broken by declared catalog order.
/// Candidates are tried in that order, falling back only on eligible transient failures
/// (RateLimited, Timeout, Unavailable, ProviderFailure), bounded by <see cref="AiRoutingOptions.MaxFallbackAttempts"/>.
/// Authentication/InvalidRequest/Cancelled failures stop the chain immediately.
/// </summary>
public sealed class AiModelRouter : IAiModelRouter
{
    private static readonly HashSet<AiFailureCategory> FallbackEligibleCategories = new()
    {
        AiFailureCategory.RateLimited,
        AiFailureCategory.Timeout,
        AiFailureCategory.Unavailable,
        AiFailureCategory.ProviderFailure
    };

    private readonly ModelRoutingOptions _modelRoutingOptions;
    private readonly AiProviderOptions _aiProviderOptions;
    private readonly AiRoutingOptions _aiRoutingOptions;
    private readonly IAiModelProviderRegistry _registry;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<AiModelRouter> _logger;

    public AiModelRouter(
        ModelRoutingOptions modelRoutingOptions,
        AiProviderOptions aiProviderOptions,
        AiRoutingOptions aiRoutingOptions,
        IAiModelProviderRegistry registry,
        IOperationalMetrics metrics,
        ILogger<AiModelRouter> logger)
    {
        _modelRoutingOptions = modelRoutingOptions;
        _aiProviderOptions = aiProviderOptions;
        _aiRoutingOptions = aiRoutingOptions;
        _registry = registry;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<AiCompletionResult> ExecuteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _metrics.RecordAiRequestStarted();

        var candidates = BuildCandidateOrder(request);

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "AI routing found no available candidate for capability {Capability} (correlation {CorrelationId}).",
                request.Capability, request.CorrelationId);

            var noCandidateResult = new AiCompletionResult(
                false, null, ProviderId: "none", ModelId: "none", request.CorrelationId, DurationMs: 0,
                FailureCategory: AiFailureCategory.Unavailable,
                ErrorMessage: $"No available, capability-compatible model is registered for '{request.Capability}'.");

            _metrics.RecordAiRequestFailed();
            return noCandidateResult;
        }

        var maxAttempts = Math.Min(candidates.Count, _aiRoutingOptions.MaxFallbackAttempts);
        AiCompletionResult? lastResult = null;

        for (var attemptIndex = 0; attemptIndex < maxAttempts; attemptIndex++)
        {
            var candidateModelId = candidates[attemptIndex];

            if (!_registry.TryResolveModel(candidateModelId, out var provider) || provider is null)
            {
                _logger.LogWarning(
                    "AI routing candidate model {ModelId} has no registered provider; skipping.", candidateModelId);
                continue;
            }

            if (attemptIndex > 0)
            {
                _metrics.RecordAiRoutingFallback();
                _logger.LogInformation(
                    "AI routing fallback #{Attempt} to model {ModelId} (provider {ProviderId}) for capability {Capability}, correlation {CorrelationId}: previous candidate failed with {FailureCategory}.",
                    attemptIndex, candidateModelId, provider.ProviderId, request.Capability, request.CorrelationId,
                    lastResult?.FailureCategory);
            }

            AiCompletionResult result;

            try
            {
                result = await provider.CompleteAsync(
                    request with { PreferredModelId = candidateModelId },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _metrics.RecordAiRequestFailed();
                return new AiCompletionResult(
                    false, null, provider.ProviderId, candidateModelId, request.CorrelationId, DurationMs: 0,
                    FailureCategory: AiFailureCategory.Cancelled,
                    ErrorMessage: "Operation was cancelled.");
            }

            if (result.Success)
            {
                _metrics.RecordAiRequestSucceeded(result.DurationMs);
                return result;
            }

            lastResult = result;
            _metrics.RecordAiProviderFailure();

            if (result.FailureCategory == AiFailureCategory.RateLimited)
            {
                _metrics.RecordAiRateLimited();
            }

            var eligibleForFallback = result.FailureCategory.HasValue
                && FallbackEligibleCategories.Contains(result.FailureCategory.Value);

            if (!eligibleForFallback)
            {
                _logger.LogWarning(
                    "AI request for capability {Capability} (correlation {CorrelationId}) failed with non-retryable category {FailureCategory}: {ErrorMessage}",
                    request.Capability, request.CorrelationId, result.FailureCategory, result.ErrorMessage);

                _metrics.RecordAiRequestFailed();
                return result;
            }
        }

        _logger.LogWarning(
            "AI routing exhausted {AttemptCount} candidate(s) for capability {Capability}, correlation {CorrelationId}.",
            maxAttempts, request.Capability, request.CorrelationId);

        _metrics.RecordAiRequestFailed();
        return lastResult!;
    }

    private List<string> BuildCandidateOrder(AiCompletionRequest request)
    {
        var capabilityName = request.Capability.ToString();
        var ordered = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.PreferredModelId))
        {
            ordered.Add(request.PreferredModelId);
        }

        if (_aiRoutingOptions.CapabilityPreferredModel.TryGetValue(capabilityName, out var preferredForCapability)
            && !string.IsNullOrWhiteSpace(preferredForCapability))
        {
            ordered.Add(preferredForCapability);
        }

        var priorityOrdered = _modelRoutingOptions.Models
            .Where(m => m.IsAvailable)
            .Where(m => m.Capabilities.Any(c => string.Equals(c, capabilityName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => GetProviderPriority(m.Provider))
            .Select(m => m.ModelId);

        foreach (var modelId in priorityOrdered)
        {
            ordered.Add(modelId);
        }

        // De-duplicate while preserving precedence order, then keep only
        // capability-compatible + available + registered candidates.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var modelId in ordered)
        {
            if (!seen.Add(modelId))
            {
                continue;
            }

            if (!IsCapabilityCompatibleAndAvailable(modelId, capabilityName))
            {
                continue;
            }

            result.Add(modelId);
        }

        return result;
    }

    private bool IsCapabilityCompatibleAndAvailable(string modelId, string capabilityName)
    {
        var model = _modelRoutingOptions.Models
            .FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        return model is not null
            && model.IsAvailable
            && model.Capabilities.Any(c => string.Equals(c, capabilityName, StringComparison.OrdinalIgnoreCase));
    }

    private int GetProviderPriority(string providerName)
    {
        return _aiProviderOptions.Providers.TryGetValue(providerName, out var settings)
            ? settings.Priority
            : 0;
    }
}
