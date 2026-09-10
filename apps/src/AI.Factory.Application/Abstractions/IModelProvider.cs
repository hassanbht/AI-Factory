namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Task capability an AI completion request is being made for. Drives
/// capability-based routing in <see cref="IAiModelRouter"/> — a provider/model
/// is never selected for a capability it has not declared support for.
/// </summary>
public enum AiCapability
{
    GeneralChat,
    CodeGeneration,
    CodeReview,
    Planning,
    Reasoning,
    Summarization,
    Embedding
}

/// <summary>
/// Normalized, provider-neutral failure classification. Infrastructure
/// providers must translate every provider-specific error (HTTP status,
/// SDK exception, etc.) into one of these — Application code never sees a
/// provider's own exception types.
/// </summary>
public enum AiFailureCategory
{
    Authentication,
    RateLimited,
    Timeout,
    Unavailable,
    InvalidRequest,
    ProviderFailure,
    Cancelled
}

/// <summary>
/// Provider-neutral AI completion request. Deliberately single-turn today
/// (<see cref="Prompt"/> + optional <see cref="Context"/>) rather than a
/// full message history — <see cref="Context"/> is additive so a future
/// multi-message/conversation shape can be introduced without breaking this
/// contract.
/// </summary>
public sealed record AiCompletionRequest(
    Guid CorrelationId,
    AiCapability Capability,
    string? SystemInstructions,
    string Prompt,
    string? Context = null,
    string? PreferredModelId = null,
    int? MaxOutputTokens = null,
    double? Temperature = null);

/// <summary>
/// Provider-neutral AI completion result. <see cref="Success"/> false always
/// carries a normalized <see cref="FailureCategory"/> — never a raw
/// provider exception.
/// </summary>
public sealed record AiCompletionResult(
    bool Success,
    string? Content,
    string ProviderId,
    string ModelId,
    Guid CorrelationId,
    long DurationMs,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    AiFailureCategory? FailureCategory = null,
    string? ErrorMessage = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// A single AI model provider/model pairing capable of executing completion
/// requests. Implementations live in Infrastructure; Application depends
/// only on this interface.
/// </summary>
public interface IModelProvider
{
    /// <summary>Stable identifier matching an <c>AiProviders:Providers</c> configuration key.</summary>
    string ProviderId { get; }

    /// <summary>Capabilities this provider instance advertises support for.</summary>
    IReadOnlyCollection<string> SupportedCapabilities { get; }

    Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal signal used only between an <see cref="IModelProvider"/> and its
/// resilience decorator to carry a normalized, retry-eligible failure result
/// through <c>IResilienceExecutor</c>'s exception-based retry loop. Never
/// escapes the decorator — callers only ever see <see cref="AiCompletionResult"/>.
/// </summary>
public sealed class AiProviderTransientException : Exception
{
    public AiCompletionResult FailureResult { get; }

    public AiProviderTransientException(AiCompletionResult failureResult)
        : base(failureResult.ErrorMessage ?? "Transient AI provider failure.")
    {
        FailureResult = failureResult;
    }
}
