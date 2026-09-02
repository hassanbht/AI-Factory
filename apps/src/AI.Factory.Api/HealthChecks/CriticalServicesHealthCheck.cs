using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>
/// Readiness only — proves the DI container can actually resolve the
/// services the pipeline depends on (a cheap lookup, never invokes them),
/// catching a broken/incomplete registration before a real request would.
/// </summary>
public sealed class CriticalServicesHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;

    public CriticalServicesHealthCheck(IServiceProvider services)
    {
        _services = services;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();

        CheckResolvable<ITaskStateManager>(missing);
        CheckResolvable<IAgentExecutor>(missing);
        CheckResolvable<IModelRouter>(missing);
        CheckResolvable<IAutonomousCodingOrchestrator>(missing);

        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy("All critical services are registered.")
            : HealthCheckResult.Unhealthy($"Missing service registration(s): {string.Join(", ", missing)}"));
    }

    private void CheckResolvable<T>(List<string> missing) where T : notnull
    {
        if (_services.GetService<T>() is null)
        {
            missing.Add(typeof(T).Name);
        }
    }
}
