namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Normalised failure taxonomy for a model gateway rejection, derived only from an HTTP status
/// the runtime explicitly reported (see ModelGatewayFailureClassifier). Cancellation is not a
/// gateway reason — it is <c>ExecutionOutcomeStatus.Cancelled</c>.
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
    GatewayUnavailable,
    Unknown
}
