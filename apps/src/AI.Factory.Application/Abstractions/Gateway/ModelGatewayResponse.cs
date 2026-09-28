namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Normalised response from a model gateway.
/// Contains only provider-neutral data; vendor-specific SDK DTOs must not leak here.
/// </summary>
public sealed record ModelGatewayResponse(
    bool Success,
    string? Content,
    string GatewayKey,
    string ModelId,
    string? FinishReason,
    ModelGatewayUsage? Usage,
    ModelGatewayFailure? Failure,
    string? RequestId,
    long DurationMs,
    IReadOnlyDictionary<string, string>? ProviderMetadata = null)
{
    public bool IsSuccess => Success;
    public string? Text => Content;
    public static ModelGatewayResponse Successful(
        string gatewayKey,
        string modelId,
        string? content,
        string? finishReason,
        ModelGatewayUsage? usage,
        string? requestId,
        long durationMs,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            Success: true,
            Content: content,
            GatewayKey: gatewayKey,
            ModelId: modelId,
            FinishReason: finishReason,
            Usage: usage,
            Failure: null,
            RequestId: requestId,
            DurationMs: durationMs,
            ProviderMetadata: metadata);

    public static ModelGatewayResponse Failed(
        string gatewayKey,
        string modelId,
        ModelGatewayFailure failure,
        long durationMs,
        string? requestId = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            Success: false,
            Content: null,
            GatewayKey: gatewayKey,
            ModelId: modelId,
            FinishReason: null,
            Usage: null,
            Failure: failure,
            RequestId: requestId,
            DurationMs: durationMs,
            ProviderMetadata: metadata);
}
