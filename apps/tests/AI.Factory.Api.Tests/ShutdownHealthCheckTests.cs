using AI.Factory.Api.HealthChecks;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ShutdownHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenNotStopping()
    {
        using var shutdown = new ApplicationShutdownCoordinator();
        var healthCheck = new ShutdownHealthCheck(shutdown);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthy_WhenStopping()
    {
        using var shutdown = new ApplicationShutdownCoordinator();
        shutdown.TryBeginShutdown();

        var healthCheck = new ShutdownHealthCheck(shutdown);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("shutting down", result.Description);
    }
}
