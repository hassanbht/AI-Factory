namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Provider-neutral abstraction for AI model gateways (e.g. OpenRouter, OmniRoute).
/// Separates model gateway connectivity and transport from execution runtime selection.
/// </summary>
public interface IModelGateway
{
    /// <summary>Unique registry key of this gateway (e.g. "OpenRouter", "OmniRoute"). Case-insensitive.</summary>
    string GatewayKey { get; }

    /// <summary>Supported gateway capabilities (e.g. Chat, ToolCalling, StructuredOutput).</summary>
    ModelGatewayCapabilities Capabilities { get; }

    /// <summary>
    /// Executes a direct completion request against the model gateway.
    /// </summary>
    Task<ModelGatewayResponse> ExecuteAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats the model identifier for runtimes that require gateway-specific prefixes (e.g. OpenHands litellm prefix).
    /// </summary>
    string FormatModelIdentifier(string modelId);

    /// <summary>
    /// Returns gateway transport parameters needed by execution adapters and containers.
    /// Never includes secret values — only references secret names.
    /// </summary>
    ModelGatewayTransportInfo GetTransportInfo();
}
