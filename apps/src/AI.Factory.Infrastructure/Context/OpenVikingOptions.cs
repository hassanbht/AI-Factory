namespace AI.Factory.Infrastructure.Context;

public sealed class OpenVikingOptions
{
    /// <summary>Master switch. Off by default — no real OpenViking service is deployed yet; the system must stay fully operational without it.</summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    /// <summary>
    /// Secret name for the API key, resolved via ISecretProvider.
    /// Recommended over ApiKey.
    /// </summary>
    public string? ApiKeySecretName { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}
