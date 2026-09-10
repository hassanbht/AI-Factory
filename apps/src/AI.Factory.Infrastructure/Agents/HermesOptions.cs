namespace AI.Factory.Infrastructure.Agents;

public sealed class HermesOptions
{
    public string DockerImage { get; set; } = "nousresearch/hermes-agent:latest";

    /// <summary>Host path mounted at /opt/data (Hermes's own persistent state).</summary>
    public string DataVolume { get; set; } = string.Empty;

    /// <summary>Host directory under which a fresh isolated subfolder is created per execution, mounted at /workspace.</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 300;

    public int MaxOutputLength { get; set; } = 100_000;

    /// <summary>Docker's --network value. "none" (least privilege, no network access) is the production default.</summary>
    public string NetworkMode { get; set; } = "none";

    /// <summary>Hermes CLI --provider value (e.g. "openrouter"). Null/empty means "let Hermes use its own configured default".</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Non-secret environment variables to pass into the Hermes container beyond PATH — e.g.
    /// {"OPENROUTER_BASE_URL": "http://host.docker.internal:20128/v1"} to point the named
    /// <see cref="Provider"/> at a local OpenAI-compatible gateway instead of its real endpoint.
    /// Never put a secret value here — use <see cref="ProviderApiKeyEnvironmentVariable"/>/
    /// <see cref="ProviderApiKeySecretName"/> instead, which resolve through ISecretProvider.
    /// </summary>
    public Dictionary<string, string> AdditionalEnvironmentVariables { get; set; } = new();

    /// <summary>Container environment variable name to receive the resolved provider API key (e.g. "OPENROUTER_API_KEY").</summary>
    public string? ProviderApiKeyEnvironmentVariable { get; set; }

    /// <summary>Factory secret name (resolved via ISecretProvider) whose value becomes <see cref="ProviderApiKeyEnvironmentVariable"/> inside the container.</summary>
    public string? ProviderApiKeySecretName { get; set; }
}
