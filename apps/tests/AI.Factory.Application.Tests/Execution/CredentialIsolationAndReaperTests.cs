using System.Diagnostics;
using System.Runtime.InteropServices;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Tests.Gateway;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.Cli;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase M — (1) Hermes credential isolation: benchmark factory-T1-oldkey/T2-oldkey showed Hermes using
/// the OPENROUTER_API_KEY from the developer's ~/.hermes/.env (mounted as HERMES_HOME, loaded by Hermes with
/// override=True) instead of the key the Factory injected. (2) The local-process reaper that kills a crashed
/// Worker's CLI child — exercised against real OS processes.
/// </summary>
public sealed class CredentialIsolationAndReaperTests : IDisposable
{
    private const string FactoryKey = "sk-or-factory-selected-0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-isolation-" + Guid.NewGuid().ToString("N"));

    public CredentialIsolationAndReaperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = new();

        public string ReportJson { get; init; } = "{\"failed\":false,\"completed\":true}";

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("run"))
            {
                HermesRunReportFake.WriteRaw(request, ReportJson);
            }

            return Task.FromResult(new ProcessRunResult(0, "{}", string.Empty, TimedOut: false));
        }
    }

    private HermesAgentExecutor Hermes(CapturingRunner runner, string? secret = FactoryKey) => new(
        new HermesOptions
        {
            DockerImage = "test/hermes:pinned",
            WorkspaceRoot = Path.Combine(_root, "scratch"),
            Provider = "openrouter",
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            ProviderApiKeySecretName = "OPENROUTER_API_KEY"
        },
        runner,
        NullLogger<HermesAgentExecutor>.Instance,
        secretProvider: new FakeSecretProvider(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = secret }));

    private AgentExecutionRequest Request() =>
        new("coder", "m-1", "prompt", Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName, AgentRunId: Guid.NewGuid());

    [Fact]
    public async Task Hermes_HomeIsAPerRunAnonymousVolume_NeverAConfiguredOrUserDirectory()
    {
        var runner = new CapturingRunner();

        await Hermes(runner).ExecuteAsync(Request());

        var args = runner.Requests.Single(r => r.Arguments.Contains("run")).Arguments.ToList();
        Assert.Equal("-v", args[args.IndexOf("/opt/data") - 1]);
        Assert.DoesNotContain(args, a => a.EndsWith(":/opt/data", StringComparison.Ordinal)); // no host path / named volume
        Assert.DoesNotContain(args, a => a.Contains(".hermes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--rm", args); // the anonymous home is removed with the container
    }

    [Fact]
    public async Task Hermes_FactoryKeyReachesTheContainerOnlyByName_NeverOnTheCommandLine()
    {
        var runner = new CapturingRunner();

        await Hermes(runner).ExecuteAsync(Request());

        var run = runner.Requests.Single(r => r.Arguments.Contains("run"));
        Assert.DoesNotContain(run.Arguments, a => a.Contains(FactoryKey, StringComparison.Ordinal));
        Assert.Contains("OPENROUTER_API_KEY", run.Arguments); // `-e NAME`: docker copies it from its own env
        Assert.Equal(FactoryKey, run.EnvironmentVariables!["OPENROUTER_API_KEY"]);
        Assert.Equal(new[] { "OPENROUTER_API_KEY", "PATH" }, run.EnvironmentVariables.Keys.Order());
    }

    [Fact]
    public async Task Hermes_MissingFactoryCredential_RefusesToStart_InsteadOfRunningKeyless()
    {
        var runner = new CapturingRunner();

        var result = await Hermes(runner, secret: null).ExecuteAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("could not be resolved", result.Error);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Hermes_CleanupRemovesTheContainerAndItsAnonymousHome()
    {
        var runner = new CapturingRunner();

        await Hermes(runner).ExecuteAsync(Request());

        var cleanup = runner.Requests.Single(r => r.Arguments.Contains("rm")).Arguments;
        Assert.Equal(new[] { "rm", "-f", "-v" }, cleanup.Take(3));
    }

    [Fact]
    public async Task Hermes_RunReportMetadata_IsPersistedOnlyWhenHermesWroteIt()
    {
        var reported = new CapturingRunner
        {
            ReportJson = "{\"failed\":false,\"completed\":true,\"session_id\":\"sess-9\",\"estimated_cost_usd\":0.0042,"
                + "\"cost_status\":\"estimated\",\"api_calls\":4,\"model\":\"m-1\",\"input_tokens\":10,\"output_tokens\":5}"
        };

        var result = await Hermes(reported).ExecuteAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("sess-9", result.Report!.SessionId);
        Assert.Equal(0.0042m, result.Report.CostUsd);
        Assert.Equal("hermes:estimated_cost_usd(estimated)", result.Report.CostSource);
        Assert.Equal(4, result.Report.Turns);

        var bare = await Hermes(new CapturingRunner()).ExecuteAsync(Request());
        Assert.Null(bare.Report!.SessionId);
        Assert.Null(bare.Report.CostUsd); // not exposed: absent, never zero
        Assert.Null(bare.Usage);
    }

    // ---------------- Local-process reaper (real OS processes) ----------------

    private static Process StartLongRunningTree()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        return Process.Start(new ProcessStartInfo(
            windows ? "cmd.exe" : "sh",
            windows ? "/c ping -n 120 127.0.0.1 > NUL" : "-c \"sleep 120\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
    }

    private LocalProcessLifecycleManager Reaper(IServiceProvider? services = null) => new(
        new CliRuntimeOptions { StateRoot = Path.Combine(_root, "state") },
        (services ?? new ServiceCollection().BuildServiceProvider()).GetRequiredService<IServiceScopeFactory>(),
        NullLogger<LocalProcessLifecycleManager>.Instance);

    [Fact]
    public async Task Reaper_KillsTheOrphanedCliProcessTree_OfACrashedWorker_AndDeletesItsState()
    {
        var runId = Guid.NewGuid();
        var sandbox = CliRunSandbox.Create(Path.Combine(_root, "state"), runId, NullLogger.Instance); // never disposed: the "crash"
        using var orphan = StartLongRunningTree();
        sandbox.RecordProcess(orphan);

        await Reaper().EnsureCleanupAsync(runId);

        Assert.True(orphan.WaitForExit(10_000), "the orphaned CLI process must be terminated");
        Assert.False(Directory.Exists(sandbox.RunDirectory));
    }

    [Fact]
    public async Task Reaper_IsIdempotent_WhenTheProcessAlreadyExitedOrNothingWasLeft()
    {
        var runId = Guid.NewGuid();
        var sandbox = CliRunSandbox.Create(Path.Combine(_root, "state"), runId, NullLogger.Instance);
        using (var exited = StartLongRunningTree())
        {
            sandbox.RecordProcess(exited);
            exited.Kill(entireProcessTree: true);
            exited.WaitForExit();
        }

        var reaper = Reaper();
        await reaper.EnsureCleanupAsync(runId);
        await reaper.EnsureCleanupAsync(runId);
        await reaper.EnsureCleanupAsync(Guid.NewGuid());

        Assert.False(Directory.Exists(sandbox.RunDirectory));
    }

    [Fact]
    public async Task Reaper_NeverKillsAProcessWhoseStartTimeDoesNotMatch_RecycledPidGuard()
    {
        var runId = Guid.NewGuid();
        var runDirectory = CliRunSandbox.Create(Path.Combine(_root, "state"), runId, NullLogger.Instance).RunDirectory;
        using var unrelated = StartLongRunningTree();
        try
        {
            File.WriteAllText(Path.Combine(runDirectory, "process.json"),
                $"{{\"Pid\":{unrelated.Id},\"StartTimeUtc\":\"2000-01-01T00:00:00Z\"}}");

            await Reaper().EnsureCleanupAsync(runId);

            Assert.False(unrelated.HasExited);
        }
        finally
        {
            unrelated.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Sweep_ReapsUnknownTerminalAndStaleRuns_ButNeverAHeartbeatingOne()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var services = new ServiceCollection()
            .AddDbContext<FactoryDbContext>(o => o.UseSqlite(connection))
            .AddScoped<IAgentRunRepository, AgentRunRepository>()
            .BuildServiceProvider();

        Guid live, terminal, stale;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            db.Database.EnsureCreated();
            var definition = await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "d", AgentType = "Implementation", SystemPrompt = "x" });

            AgentRun Run(AgentRunStatus status, TimeSpan heartbeatAge)
            {
                var run = new AgentRun { AgentDefinitionId = definition.Id, Status = status, UpdatedAtUtc = DateTime.UtcNow - heartbeatAge };
                db.AgentRuns.Add(run);
                return run;
            }

            live = Run(AgentRunStatus.Running, TimeSpan.FromSeconds(10)).Id;
            terminal = Run(AgentRunStatus.Succeeded, TimeSpan.Zero).Id;
            stale = Run(AgentRunStatus.Running, TimeSpan.FromMinutes(10)).Id;
            await db.SaveChangesAsync();
        }

        var state = Path.Combine(_root, "state");
        var unknown = Guid.NewGuid();
        foreach (var id in new[] { live, terminal, stale, unknown })
        {
            CliRunSandbox.Create(state, id, NullLogger.Instance);
        }

        var foreign = Directory.CreateDirectory(Path.Combine(state, "not-a-run")).FullName;

        var swept = await Reaper(services).SweepOrphanContainersAsync();

        Assert.Equal(3, swept);
        Assert.True(Directory.Exists(CliRunSandbox.RunDirectoryFor(state, live)));
        Assert.True(Directory.Exists(foreign));
        Assert.All(new[] { terminal, stale, unknown }, id => Assert.False(Directory.Exists(CliRunSandbox.RunDirectoryFor(state, id))));
    }
}
