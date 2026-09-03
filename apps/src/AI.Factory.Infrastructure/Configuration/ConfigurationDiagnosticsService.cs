using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Resilience;
using AI.Factory.Application.Security;
using AI.Factory.Infrastructure.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// Implements safe, non-sensitive configuration diagnostics for operational observability.
/// Returns metadata describing system setup, active providers, and options validation status
/// without revealing connection strings, passwords, tokens, or raw secrets.
/// </summary>
public sealed class ConfigurationDiagnosticsService : IConfigurationDiagnosticsService
{
    private readonly IHostEnvironment _environment;
    private readonly DatabaseOptions _databaseOptions;
    private readonly SecretManagementOptions _secretOptions;
    private readonly ISecretProvider _secretProvider;
    private readonly SecurityOptions _securityOptions;
    private readonly ResilienceOptions? _resilienceOptions;
    private readonly AiProviderOptions? _aiProviderOptions;
    private readonly OpenVikingOptions? _openVikingOptions;
    private readonly IConfiguration? _configuration;

    public ConfigurationDiagnosticsService(
        IHostEnvironment environment,
        DatabaseOptions databaseOptions,
        SecretManagementOptions secretOptions,
        ISecretProvider secretProvider,
        SecurityOptions securityOptions,
        ResilienceOptions? resilienceOptions = null,
        AiProviderOptions? aiProviderOptions = null,
        OpenVikingOptions? openVikingOptions = null,
        IConfiguration? configuration = null)
    {
        _environment = environment;
        _databaseOptions = databaseOptions;
        _secretOptions = secretOptions;
        _secretProvider = secretProvider;
        _securityOptions = securityOptions;
        _resilienceOptions = resilienceOptions;
        _aiProviderOptions = aiProviderOptions;
        _openVikingOptions = openVikingOptions;
        _configuration = configuration;
    }

    public ConfigurationDiagnosticSnapshot GetSnapshot()
    {
        bool isValid = true;

        try
        {
            _databaseOptions.Validate();
            _secretOptions.Validate();
            _securityOptions.Validate();
            _resilienceOptions?.Validate();
            _aiProviderOptions?.Validate();
        }
        catch
        {
            isValid = false;
        }

        var databaseInfo = new DatabaseDiagnosticInfo(
            Configured: !string.IsNullOrWhiteSpace(_databaseOptions.ConnectionString),
            Provider: _databaseOptions.Provider,
            HasConnectionString: !string.IsNullOrWhiteSpace(_databaseOptions.ConnectionString));

        var secretInfo = new SecretManagementDiagnosticInfo(
            Enabled: _secretOptions.Enabled,
            ProviderName: _secretProvider.ProviderName,
            FailFastOnMissingSecret: _secretOptions.FailFastOnMissingSecret);

        var securityInfo = new SecurityDiagnosticInfo(
            OperationalEndpointsSecured: _securityOptions.OperationalEndpoints.RequireAccessKey,
            OperationalKeyHeader: _securityOptions.OperationalEndpoints.HeaderName,
            RateLimitingEnabled: _securityOptions.RateLimiting.Enabled,
            MaxRequestBodySizeBytes: _securityOptions.RequestLimits.MaxRequestBodySizeBytes,
            CorsEnabled: _securityOptions.Cors.Enabled,
            HttpsRedirectEnabled: _securityOptions.Https.RedirectHttpToHttps,
            HstsEnabled: _securityOptions.Https.EnableHstsInProduction);

        var resilienceProfiles = _resilienceOptions?.Profiles.Keys.OrderBy(k => k).ToList() ?? new List<string>();
        var resilienceInfo = new ResilienceDiagnosticInfo(
            ConfiguredProfilesCount: resilienceProfiles.Count,
            ProfileNames: resilienceProfiles);

        var externalProviders = new List<ProviderDiagnosticInfo>();

        if (_openVikingOptions is not null)
        {
            externalProviders.Add(new ProviderDiagnosticInfo(
                Name: "OpenViking",
                Enabled: _openVikingOptions.Enabled,
                HasSecretReference: !string.IsNullOrWhiteSpace(_openVikingOptions.ApiKeySecretName),
                SecretReferenceName: _openVikingOptions.ApiKeySecretName));
        }

        if (_aiProviderOptions?.Providers is not null)
        {
            foreach (var (name, settings) in _aiProviderOptions.Providers)
            {
                externalProviders.Add(new ProviderDiagnosticInfo(
                    Name: name,
                    Enabled: settings.Enabled,
                    HasSecretReference: !string.IsNullOrWhiteSpace(settings.ApiKeySecretName),
                    SecretReferenceName: settings.ApiKeySecretName));
            }
        }

        var configSources = new List<string>();
        if (_configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                configSources.Add(provider.GetType().Name);
            }
        }

        return new ConfigurationDiagnosticSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            EnvironmentName: _environment.EnvironmentName,
            IsValid: isValid,
            Database: databaseInfo,
            SecretManagement: secretInfo,
            Security: securityInfo,
            Resilience: resilienceInfo,
            ExternalProviders: externalProviders,
            ConfigurationSources: configSources);
    }
}
