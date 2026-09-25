using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase D.1 — Integration tests validating real Docker container lifecycle,
/// naming, metadata labels, cleanup on startup failure, timeout, cancellation,
/// and safe startup orphan sweep.
/// </summary>
public sealed class DockerLifecycleIntegrationTests
{
    private static bool IsDockerDaemonRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<string>> GetFactoryContainersAsync(Guid agentRunId)
    {
        var containerName = DockerContainerNaming.GetContainerName(agentRunId);
        var psi = new ProcessStartInfo("docker", $"ps -a --filter name={containerName} --format {{{{Score}}}}{{{{.Names}}}}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Use simpler format
        psi.Arguments = $"ps -a --filter name={containerName} --format {{{{.Names}}}}";
        using var proc = Process.Start(psi);
        if (proc is null) return new();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Equals(containerName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [Fact]
    public async Task StartupFailure_DueToMissingNetwork_CleansUpCreatedContainer_LeavingZeroContainers()
    {
        if (!IsDockerDaemonRunning()) return;

        var runId = Guid.NewGuid();
        var containerName = DockerContainerNaming.GetContainerName(runId);

        var runner = new SystemProcessRunner();
        var hermesOptions = new HermesOptions
        {
            DockerImage = "nousresearch/hermes-agent:0.20.6",
            NetworkMode = "nonexistent-network-" + Guid.NewGuid().ToString("N")[..8],
            TimeoutSeconds = 15
        };

        var executor = new HermesAgentExecutor(
            hermesOptions,
            runner,
            NullLogger<HermesAgentExecutor>.Instance);

        var request = new AgentExecutionRequest(
            Agent: "test-coder",
            Model: "test-model",
            Prompt: "hello",
            WorkingDirectory: Path.GetTempPath(),
            AgentRunId: runId);

        var result = await executor.ExecuteAsync(request);

        // Docker exit code 125 for missing network / container configuration failure
        Assert.False(result.Success);

        // Assert 0 containers remain for this AgentRun
        var remaining = await GetFactoryContainersAsync(runId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Timeout_CleansUpContainer_LeavingZeroContainers()
    {
        if (!IsDockerDaemonRunning()) return;

        var runId = Guid.NewGuid();
        var runner = new SystemProcessRunner();

        // Set a 1-second timeout so it times out almost immediately
        var hermesOptions = new HermesOptions
        {
            DockerImage = "nousresearch/hermes-agent:0.20.6",
            NetworkMode = "bridge",
            TimeoutSeconds = 1
        };

        var executor = new HermesAgentExecutor(
            hermesOptions,
            runner,
            NullLogger<HermesAgentExecutor>.Instance);

        var request = new AgentExecutionRequest(
            Agent: "test-coder",
            Model: "test-model",
            Prompt: "sleep 10",
            WorkingDirectory: Path.GetTempPath(),
            AgentRunId: runId,
            Deadline: DateTimeOffset.UtcNow.AddMilliseconds(50));

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);

        // Wait briefly for Docker daemon cleanup
        await Task.Delay(1000);

        var remaining = await GetFactoryContainersAsync(runId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Cancellation_CleansUpContainer_LeavingZeroContainers()
    {
        if (!IsDockerDaemonRunning()) return;

        var runId = Guid.NewGuid();
        var runner = new SystemProcessRunner();

        var hermesOptions = new HermesOptions
        {
            DockerImage = "nousresearch/hermes-agent:0.20.6",
            NetworkMode = "bridge",
            TimeoutSeconds = 30
        };

        var executor = new HermesAgentExecutor(
            hermesOptions,
            runner,
            NullLogger<HermesAgentExecutor>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var request = new AgentExecutionRequest(
            Agent: "test-coder",
            Model: "test-model",
            Prompt: "sleep 30",
            WorkingDirectory: Path.GetTempPath(),
            AgentRunId: runId);

        try
        {
            await executor.ExecuteAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Wait briefly for Docker daemon cleanup
        await Task.Delay(1000);

        var remaining = await GetFactoryContainersAsync(runId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task OrphanSweep_RemovesStaleFactoryContainer_AndPreservesForeignContainer()
    {
        if (!IsDockerDaemonRunning()) return;

        var staleRunId = Guid.NewGuid();
        var staleContainerName = DockerContainerNaming.GetContainerName(staleRunId);
        var foreignContainerName = "foreign-unmanaged-test-" + Guid.NewGuid().ToString("N")[..8];

        // 1. Create a stale Factory-owned container (not active in DB)
        var createFactoryCmd = Process.Start(new ProcessStartInfo("docker",
            $"create --name {staleContainerName} --label factory=ai-factory --label managed=true --label agent-run-id={staleRunId} --label adapter=Hermes alpine sleep 60")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        if (createFactoryCmd != null) await createFactoryCmd.WaitForExitAsync();

        // 2. Create a foreign / unmanaged container (no factory labels)
        var createForeignCmd = Process.Start(new ProcessStartInfo("docker",
            $"create --name {foreignContainerName} alpine sleep 60")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        if (createForeignCmd != null) await createForeignCmd.WaitForExitAsync();

        try
        {
            // Set up DI with in-memory SQLite DB where staleRunId does NOT exist
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<FactoryDbContext>(o => o.UseSqlite(connection));
            services.AddScoped<IAgentRunRepository, AgentRunRepository>();
            var sp = services.BuildServiceProvider();

            using (var initScope = sp.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<FactoryDbContext>();
                db.Database.EnsureCreated();
            }

            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var runner = new SystemProcessRunner();
            var lifecycleManager = new DockerContainerLifecycleManager(
                runner,
                scopeFactory,
                NullLogger<DockerContainerLifecycleManager>.Instance);

            var swept = await lifecycleManager.SweepOrphanContainersAsync();

            Assert.True(swept >= 1);

            // Stale container should now be deleted
            var remainingStale = await GetFactoryContainersAsync(staleRunId);
            Assert.Empty(remainingStale);

            // Foreign container MUST still exist!
            var checkForeign = Process.Start(new ProcessStartInfo("docker", $"ps -a --filter name={foreignContainerName} --format {{{{.Names}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            var foreignStdout = await checkForeign!.StandardOutput.ReadToEndAsync();
            await checkForeign.WaitForExitAsync();
            Assert.Contains(foreignContainerName, foreignStdout);
        }
        finally
        {
            // Cleanup foreign container
            var cleanupForeign = Process.Start(new ProcessStartInfo("docker", $"rm -f {foreignContainerName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (cleanupForeign != null) await cleanupForeign.WaitForExitAsync();
        }
    }
}
