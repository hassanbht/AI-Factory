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
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase D.1 — Integration tests validating real Docker container lifecycle,
/// naming, metadata labels, cleanup on startup failure, timeout, cancellation,
/// and safe startup orphan sweep.
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class DockerLifecycleIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public DockerLifecycleIntegrationTests(ITestOutputHelper output) => _output = output;

    /// <summary>Adds `--filter name=&lt;container&gt;` to the sweep's `docker ps` so only that container can be listed (and so removed).</summary>
    private sealed class ScopedListingRunner : IProcessRunner
    {
        private readonly IProcessRunner _inner;
        private readonly string _containerName;

        public ScopedListingRunner(IProcessRunner inner, string containerName)
        {
            _inner = inner;
            _containerName = containerName;
        }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            _inner.RunAsync(
                request.Arguments.FirstOrDefault() == "ps"
                    ? request with { Arguments = request.Arguments.Concat(new[] { "--filter", $"name={_containerName}" }).ToList() }
                    : request,
                cancellationToken);
    }

    private static bool IsSameProcessAlive(int pid, DateTime startTime)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime == startTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
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

    [DockerFact]
    public async Task StartupFailure_DueToMissingNetwork_CleansUpCreatedContainer_LeavingZeroContainers()
    {
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

    [DockerFact]
    public async Task Timeout_CleansUpContainer_LeavingZeroContainers()
    {
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

        // Phase O — no settle delay: the executor's own cleanup converges before it returns.
        var remaining = await GetFactoryContainersAsync(runId);
        Assert.Empty(remaining);
    }

    [DockerFact]
    public async Task Cancellation_CleansUpContainer_LeavingZeroContainers()
    {
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

        // Phase O — no settle delay. The old test waited a second and then failed intermittently: cancelling a few
        // hundred ms after launch killed the `docker run` client before the daemon had created the container, the single
        // `docker rm -f` found nothing, and the container appeared afterwards. The executor's cleanup now reconciles for a
        // bounded window, so the container is gone — and stays gone — by the time ExecuteAsync has returned.
        var remaining = await GetFactoryContainersAsync(runId);
        Assert.Empty(remaining);

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(await GetFactoryContainersAsync(runId));
    }

    /// <summary>
    /// Phase O — the real daemon, four launches cancelled at different moments after `docker run` started (0 ms = the client is
    /// killed at once). Each is cleaned up by the bounded reconciliation; then the Worker's periodic orphan sweep is emulated
    /// PER RUN (the same `docker rm -f -v &lt;name&gt;` it issues for a terminal run — never the global sweep, which would also
    /// remove a developer's live Factory containers) until nothing of these runs is left. Asserted: no cleanup fails, each stays
    /// bounded, the system converges to zero, and a foreign container is untouched. How many containers needed the sweep is
    /// reported, not asserted: on a loaded daemon the creation can land after the reconcile window, which is exactly what the
    /// sweep is for (the deterministic proof of the window itself is DockerCleanupReconciliationTests).
    /// </summary>
    [DockerFact]
    public async Task CancelBeforeContainerCreation_AtVaryingMoments_ConvergesToZeroFactoryContainers()
    {
        var runner = new SystemProcessRunner();
        var cancelAfterMs = new[] { 0, 40, 120, 350 };
        var foreignName = "aif-o-foreign-" + Guid.NewGuid().ToString("N")[..8];
        var runIds = cancelAfterMs.Select(_ => Guid.NewGuid()).ToArray();
        var names = runIds.Select(DockerContainerNaming.GetContainerName).ToArray();
        var launchClients = new (int Pid, DateTime StartTime)[cancelAfterMs.Length];

        var createForeign = Process.Start(new ProcessStartInfo("docker", $"create --name {foreignName} alpine sleep 60")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        await createForeign!.WaitForExitAsync();

        try
        {
            var results = await Task.WhenAll(cancelAfterMs.Select(async (delayMs, i) =>
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(delayMs);

                try
                {
                    await runner.RunAsync(
                        new ProcessRunRequest(
                            "docker",
                            new[]
                            {
                                "run", "--rm", "--name", names[i],
                                "--label", "factory=ai-factory", "--label", "managed=true", "--label", $"agent-run-id={runIds[i]}",
                                "alpine", "sleep", "60"
                            },
                            TimeSpan.FromSeconds(60),
                            OnStarted: process => launchClients[i] = (process.Id, process.StartTime)),
                        cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the launch client was killed.
                }

                return await DockerContainerCleanup.RemoveAsync(runner, names[i], NullLogger.Instance, reconcile: true);
            }));

            for (var i = 0; i < results.Length; i++)
            {
                _output.WriteLine($"cancel@{cancelAfterMs[i]}ms: {results[i].Outcome} after {results[i].Attempts} attempts, {results[i].Elapsed.TotalSeconds:F1}s");
            }

            // No Factory-owned process survives either: every `docker run` launch client was killed (pid and start time, so a
            // recycled pid cannot count as alive).
            foreach (var (pid, startTime) in launchClients)
            {
                Assert.NotEqual(0, pid); // the client was started
                Assert.False(IsSameProcessAlive(pid, startTime), $"launch client {pid} is still running");
            }

            Assert.All(results, r => Assert.NotEqual(ContainerCleanupOutcome.Failed, r.Outcome));
            Assert.All(results, r => Assert.True(r.Elapsed < TimeSpan.FromSeconds(40), $"reconciliation must stay bounded, took {r.Elapsed}"));

            // The sweep backstop: a container the daemon created after the window belongs to a cancelled (terminal) run.
            var late = 0;
            var convergence = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var remaining = new List<string>();
                foreach (var name in names)
                {
                    if ((await GetFactoryContainersAsync(Guid.Parse(name[DockerContainerNaming.ContainerNamePrefix.Length..]))).Count > 0)
                    {
                        remaining.Add(name);
                    }
                }

                if (remaining.Count == 0 && convergence.Elapsed > TimeSpan.FromSeconds(3))
                {
                    break; // empty, and still empty a few seconds after the last removal
                }

                Assert.True(convergence.Elapsed < TimeSpan.FromSeconds(90), $"still not converged: {string.Join(", ", remaining)}");

                foreach (var name in remaining)
                {
                    late++;
                    await DockerContainerCleanup.RemoveAsync(runner, name, NullLogger.Instance, reconcile: false);
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            _output.WriteLine($"{late} container(s) appeared after the reconcile window and were removed by the sweep backstop.");

            var foreign = Process.Start(new ProcessStartInfo("docker", $"ps -a --filter name={foreignName} --format {{{{.Names}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            var foreignOut = await foreign!.StandardOutput.ReadToEndAsync();
            await foreign.WaitForExitAsync();
            Assert.Contains(foreignName, foreignOut);
        }
        finally
        {
            var cleanup = Process.Start(new ProcessStartInfo("docker", $"rm -f -v {foreignName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (cleanup is not null)
            {
                await cleanup.WaitForExitAsync();
            }

            // Anything of ours that survived an assertion failure must not outlive the test.
            foreach (var name in names)
            {
                await DockerContainerCleanup.RemoveAsync(runner, name, NullLogger.Instance, reconcile: false);
            }
        }
    }

    [DockerFact]
    public async Task OrphanSweep_RemovesStaleFactoryContainer_AndPreservesForeignContainer()
    {
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

            // Phase O — the production sweep is global: against this empty database EVERY Factory-labelled agent container on the
            // daemon would look orphaned, including a developer's live Worker's active runs. The listing is therefore narrowed to
            // this test's own container; the sweep logic and the removal stay real.
            var runner = new ScopedListingRunner(new SystemProcessRunner(), staleContainerName);
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
