namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed options for secret management.
/// Configures provider selection, fail-fast behavior, and reloading.
/// </summary>
public sealed class SecretManagementOptions
{
    public const string SectionName = "SecretManagement";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Provider type: "Environment", "InMemory", or "Composite".
    /// </summary>
    public string Provider { get; set; } = "Environment";

    /// <summary>
    /// When true, a missing required secret causes fail-fast behavior instead of silent fallback.
    /// </summary>
    public bool FailFastOnMissingSecret { get; set; } = true;

    public void Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(Provider))
        {
            throw new InvalidOperationException("SecretManagement:Provider must be specified when secret management is enabled.");
        }
    }
}
