using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ContainerConfigurationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void DockerfileApi_IsProductionReady_AndNonRoot()
    {
        var dockerfilePath = Path.Combine(RepoRoot, "Dockerfile.api");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile.api does not exist at {dockerfilePath}");

        var content = File.ReadAllText(dockerfilePath);

        // Multi-stage verification
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base", content);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", content);
        Assert.Contains("FROM build AS publish", content);
        Assert.Contains("FROM base AS final", content);

        // Non-root execution
        Assert.Contains("USER $APP_UID", content);

        // Health check configured
        Assert.Contains("HEALTHCHECK", content);
        Assert.Contains("http://localhost:8080/health/live", content);

        // Signal-compatible exec entrypoint (not shell form)
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"AI.Factory.Api.dll\"]", content);

        // Exposed port
        Assert.Contains("EXPOSE 8080", content);
    }

    [Fact]
    public void DockerfileWorker_IsProductionReady_AndUsesLivenessHealthCheck()
    {
        var dockerfilePath = Path.Combine(RepoRoot, "Dockerfile.worker");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile.worker does not exist at {dockerfilePath}");

        var content = File.ReadAllText(dockerfilePath);

        // Multi-stage verification
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base", content);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", content);
        Assert.Contains("FROM build AS publish", content);
        Assert.Contains("FROM base AS final", content);

        // Non-root execution
        Assert.Contains("USER $APP_UID", content);

        // Health check configured with liveness, NOT readiness
        Assert.Contains("HEALTHCHECK", content);
        Assert.Contains("worker.live", content);

        // Signal-compatible exec entrypoint
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"AI.Factory.Worker.dll\"]", content);
    }

    [Fact]
    public void DockerCompose_DefinesProductionTopology_WithDedicatedMigrator()
    {
        var composePath = Path.Combine(RepoRoot, "docker-compose.yml");
        Assert.True(File.Exists(composePath), $"docker-compose.yml does not exist at {composePath}");

        var content = File.ReadAllText(composePath);

        // Services defined
        Assert.Contains("migrator:", content);
        Assert.Contains("api:", content);
        Assert.Contains("worker:", content);

        // Dedicated migration runner with --migrate-only
        Assert.Contains("--migrate-only", content);

        // Startup ordering: API and Worker wait for migration completion
        Assert.Contains("service_completed_successfully", content);

        // Worker healthcheck in compose must use liveness (worker.live), not readiness
        Assert.Contains("worker.live", content);

        // API healthcheck targets valid liveness endpoint
        Assert.Contains("/health/live", content);

        // Separate persistent data and backup volumes
        Assert.Contains("ai_factory_data:", content);
        Assert.Contains("ai_factory_backups:", content);
        Assert.Contains("ai_factory_production_data", content);
        Assert.Contains("ai_factory_production_backups", content);

        // Graceful shutdown stop_grace_period
        Assert.Contains("stop_grace_period: 30s", content);
        Assert.Contains("stop_grace_period: 45s", content);

        // Resource limits defined
        Assert.Contains("resources:", content);
        Assert.Contains("limits:", content);
        Assert.Contains("reservations:", content);

        // Secrets are not hardcoded (interpolated via environment variables)
        Assert.Contains("${OPENROUTER_API_KEY:-}", content);
        Assert.Contains("${OPERATIONAL_ACCESS_KEY:-}", content);
        Assert.DoesNotContain("sk-or-v1-", content);
    }
}
