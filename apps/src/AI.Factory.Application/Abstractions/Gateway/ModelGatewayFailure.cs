namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Normalised failure taxonomy for model gateway operations.
/// </summary>
public enum ModelGatewayFailureReason
{
    Authentication,
    Authorization,
    Billing,
    RateLimited,
    InvalidRequest,
    ModelUnavailable,
    ProviderUnavailable,
    Timeout,
    Cancellation,
    TransportFailure,
    GatewayUnavailable,
    UnsupportedFeature,
    Unknown
}

public sealed record ModelGatewayFailure(
    ModelGatewayFailureReason Reason,
    string Message,
    int? HttpStatusCode = null,
    TimeSpan? RetryAfter = null)
{
    public int? StatusCode => HttpStatusCode;
}
