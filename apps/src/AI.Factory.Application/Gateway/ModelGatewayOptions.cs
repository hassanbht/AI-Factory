namespace AI.Factory.Application.Gateway;

/// <summary>
/// Strongly typed configuration options for AI Model Gateways.
/// Bound from configuration section "ModelGateway".
/// </summary>
public sealed class ModelGatewayOptions
{
    public const string SectionName = "ModelGateway";

    /// <summary>Default gateway key when none is specified in ModelRoute or ExecutionProfile (e.g. "OpenRouter").</summary>
    public string DefaultGateway { get; set; } = "OpenRouter";

    /// <summary>Per-gateway configuration options, keyed by gateway key (case-insensitive).</summary>
    public Dictionary<string, GatewayEntryOptions> Gateways { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultGateway))
        {
            throw new InvalidOperationException($"{nameof(DefaultGateway)} is required.");
        }

        foreach (var (key, entry) in Gateways)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Gateway key cannot be empty.");
            }

            if (entry.Enabled && string.IsNullOrWhiteSpace(entry.BaseUrl))
            {
                throw new InvalidOperationException($"Enabled gateway '{key}' must have a BaseUrl configured.");
            }
        }
    }
}

public sealed class GatewayEntryOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKeySecretName { get; set; }
    public string? EnvironmentVariableName { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}
