using AI.Factory.Application.Abstractions.Gateway;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// OmniRoute (an OpenAI-compatible gateway, local by default) as a model-gateway transport:
/// endpoint, credential reference, Hermes CLI provider and LiteLLM model route. The runtimes
/// send the model requests (Phase K); this type never holds or returns a secret value.
/// </summary>
public sealed class OmniRouteGateway : IModelGateway
{
    public const string Key = "OmniRoute";
    public const string DefaultBaseUrl = "http://localhost:20128/v1";
    public const string DefaultApiKeySecretName = "OMNIROUTE_API_KEY";

    private const string LiteLlmPrefix = "openai/";

    private readonly string _baseUrl;
    private readonly string _apiKeySecretName;

    public string GatewayKey => Key;

    public OmniRouteGateway(string? baseUrl = null, string? apiKeySecretName = null)
    {
        _baseUrl = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.TrimEnd('/') : DefaultBaseUrl;
        _apiKeySecretName = !string.IsNullOrWhiteSpace(apiKeySecretName) ? apiKeySecretName : DefaultApiKeySecretName;
    }

    /// <summary>
    /// LiteLLM's OpenAI-compatible route: <c>openai/&lt;model&gt;</c> against the gateway's base URL.
    /// LiteLLM strips exactly that one prefix, so OmniRoute receives the Factory model id intact —
    /// including its own provider namespace (e.g. <c>openai/qwen2.5-coder:7b</c> is sent as
    /// <c>openai/qwen2.5-coder:7b</c>). Always prefixed, never stripped or rewritten.
    /// </summary>
    public string FormatModelIdentifier(string modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? string.Empty : LiteLlmPrefix + modelId;

    public ModelGatewayTransportInfo GetTransportInfo() =>
        new(
            BaseUrl: _baseUrl,
            ApiKeySecretName: _apiKeySecretName,
            EnvironmentVariableName: "OPENAI_API_KEY",
            CliProviderName: "openai");
}
