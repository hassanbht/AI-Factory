namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Service providing sanitized operational metadata about active application configuration.
/// Exposes safe metadata for operational observability without revealing connection strings or secret values.
/// </summary>
public interface IConfigurationDiagnosticsService
{
    ConfigurationDiagnosticSnapshot GetSnapshot();
}

public sealed record ConfigurationDiagnosticSnapshot(
    DateTime GeneratedAtUtc,
    string EnvironmentName,
    bool IsValid,
    DatabaseDiagnosticInfo Database,
    SecretManagementDiagnosticInfo SecretManagement,
    SecurityDiagnosticInfo Security,
    ResilienceDiagnosticInfo Resilience,
    IReadOnlyList<ProviderDiagnosticInfo> ExternalProviders,
    IReadOnlyList<string> ConfigurationSources);

public sealed record DatabaseDiagnosticInfo(
    bool Configured,
    string Provider,
    bool HasConnectionString);

public sealed record SecretManagementDiagnosticInfo(
    bool Enabled,
    string ProviderName,
    bool FailFastOnMissingSecret);

public sealed record SecurityDiagnosticInfo(
    bool OperationalEndpointsSecured,
    string OperationalKeyHeader,
    bool RateLimitingEnabled,
    long MaxRequestBodySizeBytes,
    bool CorsEnabled,
    bool HttpsRedirectEnabled,
    bool HstsEnabled);

public sealed record ResilienceDiagnosticInfo(
    int ConfiguredProfilesCount,
    IReadOnlyList<string> ProfileNames);

public sealed record ProviderDiagnosticInfo(
    string Name,
    bool Enabled,
    bool HasSecretReference,
    string? SecretReferenceName);
