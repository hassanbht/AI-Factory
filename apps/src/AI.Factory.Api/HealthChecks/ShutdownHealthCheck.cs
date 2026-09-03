using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>
/// Readiness health check that transitions to Unhealthy immediately upon shutdown initiation,
/// signalling load balancers to stop directing incoming traffic to this instance.
/// </summary>
public sealed class ShutdownHealthCheck : IHealthCheck
{
    private readonly IApplicationShutdown _shutdown;

    public ShutdownHealthCheck(IApplicationShutdown shutdown)
    {
        _shutdown = shutdown;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_shutdown.IsStopping)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Application is shutting down."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Application is running normally."));
    }
}
