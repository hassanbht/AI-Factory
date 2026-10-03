namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Provider-neutral description of an AI model gateway (e.g. OpenRouter, OmniRoute): how an
/// execution runtime reaches it. Phase K — the gateway is a transport/configuration boundary,
/// not a model-execution boundary. The production runtimes (the Hermes CLI container and the
/// OpenHands Agent Server) own their tool loop and send every model HTTP request themselves;
/// the Factory resolves the routed gateway, hands the runtime its endpoint, credential
/// reference and model identifier, and interprets the failure the runtime reports.
/// </summary>
public interface IModelGateway
{
    /// <summary>Unique registry key of this gateway (e.g. "OpenRouter", "OmniRoute"). Case-insensitive.</summary>
    string GatewayKey { get; }

    /// <summary>
    /// The LiteLLM model identifier that addresses <paramref name="modelId"/> through this gateway,
    /// for runtimes that reach the gateway through LiteLLM (OpenHands). Deterministic; the
    /// Factory model id itself is never rewritten, only prefixed with the LiteLLM provider route.
    /// </summary>
    string FormatModelIdentifier(string modelId);

    /// <summary>
    /// Returns gateway transport parameters needed by execution adapters and containers.
    /// Never includes secret values — only references secret names.
    /// </summary>
    ModelGatewayTransportInfo GetTransportInfo();
}
