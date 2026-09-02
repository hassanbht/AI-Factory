using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>Readiness only — re-validates the already-bound ExecutionLimitsOptions singleton, no I/O.</summary>
public sealed class ConfigurationHealthCheck : IHealthCheck
{
    private readonly ExecutionLimitsOptions _executionLimits;

    public ConfigurationHealthCheck(ExecutionLimitsOptions executionLimits)
    {
        _executionLimits = executionLimits;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _executionLimits.Validate();
            return Task.FromResult(HealthCheckResult.Healthy("Configuration is valid."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Configuration is invalid.", ex));
        }
    }
}
