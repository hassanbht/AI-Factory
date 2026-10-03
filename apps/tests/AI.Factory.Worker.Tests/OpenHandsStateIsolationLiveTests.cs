using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Gateway;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.Infrastructure.Gateways;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase L — the real OpenHandsExecutionAdapter against the real pinned Agent Server container,
/// proving the Agent Server's own state (conversations/, bash_events/) never lands in the Project
/// workspace: no workspace contamination, a clean `git status`, an artifact set equal to the
/// project's own files, and no container left behind — for two concurrent AgentRuns.
/// No model provider is contacted: the gateway endpoint is the container's own closed loopback
/// port and the key is a fake, so the conversation ends in the runtime's error state.
/// Opt-in via AI_FACTORY_OPENHANDS_SMOKE_TEST=true (requires Docker and the pinned image).
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class OpenHandsStateIsolationLiveTests
{
    private const string FakeKey = "sk-or-v1-PHASELSTATEISOLATION00000000000000000000";

    private readonly ITestOutputHelper _output;

    public OpenHandsStateIsolationLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool Enabled =>
        bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_OPENHANDS_SMOKE_TEST"), out var enabled) && enabled;

    [Fact]
    [LiveFact("AI_FACTORY_OPENHANDS_SMOKE_TEST", RequiresDocker = true)]
    public async Task RealAgentServer_ConcurrentRuns_LeaveNoRuntimeStateInTheirWorkspaces_NorAnyContainer()
    {
        var root = Directory.CreateTempSubdirectory("aif-oh-state-").FullName;
        try
        {
            var workspaces = new[] { CreateGitWorkspace(Path.Combine(root, "a")), CreateGitWorkspace(Path.Combine(root, "b")) };
            var adapter = CreateAdapter();
            var specs = workspaces.Select(Spec).ToArray();

            var outcomes = await Task.WhenAll(specs.Select(spec => adapter.ExecuteAsync(spec)));

            for (var i = 0; i < specs.Length; i++)
            {
                // The model call was impossible, so the run must be a failure — never a success.
                Assert.Equal(ExecutionOutcomeStatus.Failed, outcomes[i].Status);

                var workspace = workspaces[i];
                Assert.False(Directory.Exists(Path.Combine(workspace, "conversations")));
                Assert.False(Directory.Exists(Path.Combine(workspace, "bash_events")));
                Assert.Equal(string.Empty, Git(workspace, "status --porcelain --untracked-files=all"));
                // Working-tree files only; .git is covered by the clean status above (and git writes transient temp files there).
                Assert.DoesNotContain(
                    Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
                        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
                    file => File.ReadAllText(file).Contains(FakeKey, StringComparison.Ordinal));

                // What the orchestrator would record on success: exactly the project's own files.
                Assert.Equal(new[] { "README.md" }, await RecordArtifactNamesAsync(workspace));

                Assert.Equal(string.Empty, Docker($"ps -a --filter name={DockerContainerNaming.GetContainerName(specs[i].AgentRunId)} --format {{{{.Names}}}}"));
            }
        }
        finally
        {
            TestTempDirectory.Delete(root, _output);
        }
    }

    private static OpenHandsExecutionAdapter CreateAdapter()
    {
        var runner = new SystemProcessRunner();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // The OpenRouter descriptor, pointed at the container's own closed loopback port: no provider is reachable.
        var options = new ModelGatewayOptions
        {
            DefaultGateway = OpenRouterGateway.Key,
            Gateways = { [OpenRouterGateway.Key] = new GatewayEntryOptions { BaseUrl = "http://127.0.0.1:9/v1" } }
        };
        var registry = new ModelGatewayRegistry(new IModelGateway[] { new OpenRouterGateway("http://127.0.0.1:9/v1") });

        return new OpenHandsExecutionAdapter(
            new OpenHandsOptions(),
            runner,
            new DockerContainerLifecycleManager(runner, scopes, NullLogger<DockerContainerLifecycleManager>.Instance),
            new FailureClassifier(),
            NullLogger<OpenHandsExecutionAdapter>.Instance,
            secretProvider: new FixedSecretProvider(),
            gatewayResolver: new ModelGatewayResolver(registry, options));
    }

    private static AgentExecutionSpec Spec(string workspace) => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        WorkItemId: Guid.NewGuid(),
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", "You are a coding assistant."),
        Instructions: "Create hello.txt containing hi.",
        Workspace: new ExecutionWorkspace(workspace),
        Model: new ModelRoute("deepseek/deepseek-v4-flash-0731", OpenRouterGateway.Key),
        Deadline: DateTimeOffset.UtcNow.AddMinutes(3));

    private static string CreateGitWorkspace(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "baseline");
        Git(path, "init -q");
        Git(path, "add README.md");
        Git(path, "-c user.name=aif -c user.email=aif@localhost commit -q -m baseline");
        return path;
    }

    private static async Task<string[]> RecordArtifactNamesAsync(string workspace)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        await using var db = new FactoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Artifact rows reference a Project/WorkItem; FK enforcement is off for this isolated check.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await new FileSystemArtifactRecorder(db).RecordWorkspaceArtifactsAsync(Guid.NewGuid(), Guid.NewGuid(), workspace);

        return await db.Artifacts.Select(a => a.Name).OrderBy(n => n).ToArrayAsync();
    }

    private static string Git(string workingDirectory, string arguments) => Run("git", arguments, workingDirectory);

    private static string Docker(string arguments) => Run("docker", arguments, null);

    private static string Run(string fileName, string arguments, string? workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{fileName} {arguments} failed: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    private sealed class FixedSecretProvider : ISecretProvider
    {
        public string ProviderName => "Fixed";

        public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(FakeKey);

        public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretResolutionResult(true, FakeKey, ProviderName, DateTime.UtcNow));
    }
}
