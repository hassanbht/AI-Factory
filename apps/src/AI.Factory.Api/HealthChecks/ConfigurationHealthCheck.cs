using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>
/// Readiness health check that validates critical application options.
/// Ensures database, secret management, security, and execution limit options are sound
/// without performing expensive remote provider I/O on every probe.
/// </summary>
public sealed class ConfigurationHealthCheck : IHealthCheck
{
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly DatabaseOptions? _databaseOptions;
    private readonly SecretManagementOptions? _secretManagementOptions;
    private readonly SecurityOptions? _securityOptions;

    public ConfigurationHealthCheck(
        ExecutionLimitsOptions executionLimits,
        DatabaseOptions? databaseOptions = null,
        SecretManagementOptions? secretManagementOptions = null,
        SecurityOptions? securityOptions = null)
    {
        _executionLimits = executionLimits;
        _databaseOptions = databaseOptions;
        _secretManagementOptions = secretManagementOptions;
        _securityOptions = securityOptions;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _executionLimits.Validate();
            _databaseOptions?.Validate();
            _secretManagementOptions?.Validate();
            _securityOptions?.Validate();

            return Task.FromResult(HealthCheckResult.Healthy("Configuration is valid."));
        }
        catch (Exception ex)
        {
            var sanitizedMessage = SensitiveDataSanitizer.Sanitize(ex.Message);
            return Task.FromResult(HealthCheckResult.Unhealthy($"Configuration is invalid: {sanitizedMessage}"));
        }
    }
}
