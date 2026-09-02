using AI.Factory.Infrastructure.Context;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>
/// Readiness only, and only registered when configured on (see
/// Program.cs's "Health:CheckOpenViking" toggle — off by default). A real
/// network round-trip here would make /health/ready slow and flaky exactly
/// when OpenViking is degraded, which defeats the point of a health probe
/// for a component that already degrades gracefully to empty results on
/// its own (see OpenVikingContextProvider) — so this only verifies
/// configuration consistency, never makes a network call.
/// </summary>
public sealed class OpenVikingHealthCheck : IHealthCheck
{
    private readonly OpenVikingOptions _options;

    public OpenVikingHealthCheck(OpenVikingOptions options)
    {
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("OpenViking is disabled — nothing to check."));
        }

        return Task.FromResult(string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? HealthCheckResult.Unhealthy("OpenViking is enabled but has no configured BaseUrl.")
            : HealthCheckResult.Healthy("OpenViking configuration is consistent."));
    }
}
