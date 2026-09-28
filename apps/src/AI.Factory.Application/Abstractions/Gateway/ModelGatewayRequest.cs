namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Provider-neutral model gateway completion request.
/// </summary>
public sealed record ModelGatewayRequest(
    string ModelId,
    IReadOnlyList<ModelGatewayMessage> Messages,
    double? Temperature = null,
    int? MaxTokens = null,
    string? SystemInstructions = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ModelGatewayMessage(
    string Role,
    string Content);
