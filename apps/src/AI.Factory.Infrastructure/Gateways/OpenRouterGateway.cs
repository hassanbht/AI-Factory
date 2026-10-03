using AI.Factory.Application.Abstractions.Gateway;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// OpenRouter (https://openrouter.ai/api/v1) as a model-gateway transport: endpoint, credential
/// reference, Hermes CLI provider and LiteLLM model route. The runtimes send the model requests
/// (Phase K); this type never holds or returns a secret value.
/// </summary>
public sealed class OpenRouterGateway : IModelGateway
{
    public const string Key = "OpenRouter";
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultApiKeySecretName = "OPENROUTER_API_KEY";

    private const string LiteLlmPrefix = "openrouter/";

    private readonly string _baseUrl;
    private readonly string _apiKeySecretName;

    public string GatewayKey => Key;

    public OpenRouterGateway(string? baseUrl = null, string? apiKeySecretName = null)
    {
        _baseUrl = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.TrimEnd('/') : DefaultBaseUrl;
        _apiKeySecretName = !string.IsNullOrWhiteSpace(apiKeySecretName) ? apiKeySecretName : DefaultApiKeySecretName;
    }

    /// <summary>
    /// LiteLLM's OpenRouter route: <c>openrouter/&lt;OpenRouter model id&gt;</c>; LiteLLM strips the
    /// prefix and sends the OpenRouter id unchanged. An id already carrying the prefix is not
    /// prefixed twice — so ids in OpenRouter's own <c>openrouter/</c> namespace (auto-routers,
    /// which the Factory forbids) are not addressable this way.
    /// </summary>
    public string FormatModelIdentifier(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        return modelId.StartsWith(LiteLlmPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId
            : LiteLlmPrefix + modelId;
    }

    public ModelGatewayTransportInfo GetTransportInfo() =>
        new(
            BaseUrl: _baseUrl,
            ApiKeySecretName: _apiKeySecretName,
            EnvironmentVariableName: "OPENROUTER_API_KEY",
            CliProviderName: "openrouter");
}
