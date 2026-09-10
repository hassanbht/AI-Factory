namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed options for external AI model providers (e.g. OpenRouter).
/// References secrets by name, never embedding actual credential values.
/// </summary>
public sealed class AiProviderOptions
{
    public const string SectionName = "AiProviders";

    public Dictionary<string, ProviderCredentialSettings> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        foreach (var (providerName, settings) in Providers)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new InvalidOperationException("AI provider name cannot be empty.");
            }

            if (settings.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ApiKeySecretName))
            {
                throw new InvalidOperationException(
                    $"AI provider '{providerName}' requires an API key but ApiKeySecretName is not configured.");
            }
        }
    }
}

public sealed class ProviderCredentialSettings
{
    public bool Enabled { get; set; } = true;

    public string? BaseUrl { get; set; }

    public bool RequiresApiKey { get; set; } = true;

    /// <summary>
    /// The name of the environment variable or secret identifier containing the API key.
    /// Actual secret values must NEVER be stored here.
    /// </summary>
    public string? ApiKeySecretName { get; set; }

    /// <summary>Which <c>IModelProvider</c> implementation to instantiate for this entry.</summary>
    public AiModelProviderKind ProviderType { get; set; } = AiModelProviderKind.OpenAiCompatible;

    /// <summary>
    /// Deterministic routing priority — higher wins among capability-compatible
    /// candidates. Ties break on declared order in ModelRouting:Models.
    /// </summary>
    public int Priority { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Selects which model-provider implementation backs a configured provider entry.</summary>
public enum AiModelProviderKind
{
    OpenAiCompatible,
    Fake
}
