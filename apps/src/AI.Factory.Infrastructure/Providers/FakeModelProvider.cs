using AI.Factory.Application.Abstractions;

namespace AI.Factory.Infrastructure.Providers;

/// <summary>
/// Deterministic, in-memory <see cref="IModelProvider"/> for tests and local
/// development without a real provider configured. No network calls, no
/// timing-sensitive behavior — the response is either a fixed echo or a
/// caller-supplied deterministic function of the request.
/// </summary>
public sealed class FakeModelProvider : IModelProvider
{
    private readonly Func<AiCompletionRequest, AiCompletionResult> _respond;

    public string ProviderId { get; }

    public IReadOnlyCollection<string> SupportedCapabilities { get; }

    public FakeModelProvider(
        string providerId,
        IEnumerable<string> supportedCapabilities,
        Func<AiCompletionRequest, AiCompletionResult>? respond = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ProviderId = providerId;
        SupportedCapabilities = supportedCapabilities.ToList();
        _respond = respond ?? DefaultRespond;
    }

    public Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_respond(request));
    }

    private AiCompletionResult DefaultRespond(AiCompletionRequest request)
    {
        return new AiCompletionResult(
            Success: true,
            Content: $"[fake:{ProviderId}] {request.Prompt}",
            ProviderId: ProviderId,
            ModelId: request.PreferredModelId ?? "fake-model",
            CorrelationId: request.CorrelationId,
            DurationMs: 1);
    }
}
