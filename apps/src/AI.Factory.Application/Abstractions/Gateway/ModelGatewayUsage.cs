namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Normalised token and cost usage reported by a model gateway.
/// All fields are optional. Missing metrics are left null (never fabricated or assumed to be 0).
/// </summary>
public sealed record ModelGatewayUsage(
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null,
    decimal? Cost = null);
