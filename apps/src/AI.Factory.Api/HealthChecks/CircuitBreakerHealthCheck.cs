using AI.Factory.Application.Resilience;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>
/// Readiness health check that monitors circuit breaker states across dependencies.
/// Degraded if any dependency circuit is Open or HalfOpen; Healthy otherwise.
/// Does not affect liveness (/health/live).
/// </summary>
public sealed class CircuitBreakerHealthCheck : IHealthCheck
{
    private readonly ICircuitBreakerRegistry _registry;

    public CircuitBreakerHealthCheck(ICircuitBreakerRegistry registry)
    {
        _registry = registry;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshots = _registry.GetAllSnapshots();

        var openCircuits = snapshots.Where(s => s.State == CircuitState.Open).ToList();
        if (openCircuits.Count > 0)
        {
            var names = string.Join(", ", openCircuits.Select(c => c.DependencyName));
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Dependency circuit(s) currently open: {names}"));
        }

        var halfOpenCircuits = snapshots.Where(s => s.State == CircuitState.HalfOpen).ToList();
        if (halfOpenCircuits.Count > 0)
        {
            var names = string.Join(", ", halfOpenCircuits.Select(c => c.DependencyName));
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Dependency circuit(s) currently half-open: {names}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("All dependency circuit breakers are closed."));
    }
}
