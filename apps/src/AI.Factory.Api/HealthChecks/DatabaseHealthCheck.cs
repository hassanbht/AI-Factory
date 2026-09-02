using AI.Factory.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Factory.Api.HealthChecks;

/// <summary>Readiness only — a lightweight CanConnectAsync probe, never a real query.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly FactoryDbContext _db;

    public DatabaseHealthCheck(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("SQLite database is reachable.")
                : HealthCheckResult.Unhealthy("SQLite database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite database check threw.", ex);
        }
    }
}
