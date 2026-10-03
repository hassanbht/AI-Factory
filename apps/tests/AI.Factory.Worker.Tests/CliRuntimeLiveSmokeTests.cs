using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Tools;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.Cli;
using AI.Factory.Infrastructure.Configuration;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase M — LIVE smoke for the Claude Code / Codex runtimes, opt-in only:
/// <c>AI_FACTORY_LIVE_SMOKE=ClaudeCode</c> (credential: ANTHROPIC_API_KEY, or CLAUDE_CODE_OAUTH_TOKEN from
/// <c>claude setup-token</c>) or <c>AI_FACTORY_LIVE_SMOKE=Codex</c> (OPENAI_API_KEY; Codex__ExecutablePath for
/// the native binary on Windows). Without the variable or the credential the test returns without doing anything.
///
/// The run goes through the real Factory path — AgentOrchestrator → AdapterDispatchingAgentRuntime → adapter →
/// isolated CLI — on a disposable git repository with a tiny deterministic task (inspect, change one source
/// file, add one test, run the tests, report). The Factory then verifies independently: build/test, workspace
/// integrity, runtime-state cleanup. Evidence is printed as one JSON line; nothing is assumed.
/// </summary>
public sealed class CliRuntimeLiveSmokeTests : IDisposable
{
    private const string FactoryModel = "factory/smoke-model";
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aif-live-smoke-" + Guid.NewGuid().ToString("N"));

    public CliRuntimeLiveSmokeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine($"cleanup: {ex.Message}");
        }
    }

    [Fact]
    public async Task LiveSmoke_TinyDeterministicTask_ThroughTheFactoryPath()
    {
        var runtime = Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_SMOKE");
        if (runtime is not (ClaudeCodeExecutionAdapter.AdapterKey or CodexExecutionAdapter.AdapterKey))
        {
            return;
        }

        var stateRoot = Path.Combine(_root, "runtime-state");
        var (adapter, credentialName) = CreateAdapter(runtime, stateRoot);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(credentialName)))
        {
            _output.WriteLine($"{runtime}: {credentialName} not set - NOT_VERIFIED (no credential).");
            return;
        }

        var workspace = CreateFixtureRepository();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        Guid runId;
        await using (var db = new FactoryDbContext(options))
        {
            db.Database.EnsureCreated();
            var project = new Project { Name = "smoke", Slug = "smoke", RootPath = workspace, Status = ProjectStatus.Implementation };
            db.Projects.Add(project);
            var definition = new AgentDefinition { Name = "smoke-impl", AgentType = "Implementation", SystemPrompt = string.Empty };
            db.AgentDefinitions.Add(definition);
            var run = new AgentRun
            {
                AgentDefinitionId = definition.Id, ProjectId = project.Id, WorkItemId = Guid.NewGuid(), Model = FactoryModel,
                Input = "In this .NET repository: add a method `public int Subtract(int a, int b) => a - b;` to the Calculator "
                    + "class in Calc/Calculator.cs, add one xunit test for it to Calc.Tests/CalculatorTests.cs, run `dotnet test`, "
                    + "and report. Change nothing else and create no other files."
            };
            db.AgentRuns.Add(run);
            run.TransitionTo(AgentRunStatus.Queued);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var execution = new ExecutionOptions
        {
            DefaultProfile = runtime,
            Profiles = { [runtime] = new ExecutionProfileOptions { Adapter = runtime } }
        };
        var stopwatch = Stopwatch.StartNew();
        AgentOrchestrationResult result;
        await using (var db = new FactoryDbContext(options))
        {
            var dispatcher = new AdapterDispatchingAgentRuntime(
                new ExecutionProfileResolver(execution, new ExecutionAdapterRegistry(
                    new AI.Factory.Application.Abstractions.Execution.IExecutionAdapter[] { adapter })),
                new FailureClassifier(), NullLogger<AdapterDispatchingAgentRuntime>.Instance);
            result = await new AgentOrchestrator(
                new AgentRunRepository(db), new ProjectRepository(db),
                new AgentToolRegistry(Array.Empty<IAgentTool>(), NullLogger<AgentToolRegistry>.Instance),
                new LocalAgentMemoryProvider(db), dispatcher, new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
                new AgentInstructionBuilder(), new StructuredAgentResultParser(), NullLogger<AgentOrchestrator>.Instance,
                workerOptions: new AgentWorkerOptions { ExecutionTimeout = TimeSpan.FromMinutes(15) }).ExecuteAsync(runId);
        }

        stopwatch.Stop();

        AgentRun persisted;
        await using (var db = new FactoryDbContext(options))
        {
            persisted = await db.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
        }

        var metadata = JsonSerializer.Deserialize<StructuredAgentResult>(persisted.StructuredResult!, StructuredAgentResult.JsonOptions)!.Metadata!;
        var changes = await new GitWorkspaceIntegrityInspector(new SystemProcessRunner(), NullLogger<GitWorkspaceIntegrityInspector>.Instance)
            .GetChangesAsync(workspace);
        var violations = WorkspaceIntegrityPolicy.Evaluate(changes!);
        var (testExit, testTail) = Run("dotnet", workspace, "test", "--nologo");
        var stateLeft = Directory.Exists(stateRoot) && Directory.EnumerateFileSystemEntries(stateRoot).Any();

        var evidence = new Dictionary<string, object?>
        {
            ["runtime"] = runtime,
            ["routedModel"] = FactoryModel,
            ["gateway"] = metadata.GetValueOrDefault("Gateway"),
            ["reportedModel"] = metadata.GetValueOrDefault("RuntimeReportedModel"),
            ["durationMs"] = stopwatch.ElapsedMilliseconds,
            ["exitReason"] = metadata.GetValueOrDefault("ExitReason"),
            ["agentRunStatus"] = persisted.Status.ToString(),
            ["error"] = persisted.ErrorMessage,
            ["usage"] = new { persisted.PromptTokens, persisted.CompletionTokens, persisted.TotalTokens },
            ["costUsd"] = metadata.GetValueOrDefault("CostUsd") ?? "NOT_EXPOSED",
            ["sessionId"] = metadata.GetValueOrDefault("RuntimeSessionId") ?? "NOT_EXPOSED",
            ["changedFiles"] = changes!.Select(c => $"{c.Kind}:{c.Path}").ToArray(),
            ["integrityViolations"] = violations,
            ["factoryDotnetTestExitCode"] = testExit,
            ["runtimeStateLeft"] = stateLeft
        };
        _output.WriteLine("LIVE_SMOKE " + JsonSerializer.Serialize(evidence));
        _output.WriteLine("factory dotnet test tail: " + testTail);

        // The Factory's own checks are the verdict, not the agent's report.
        Assert.True(result.Executed);
        Assert.Equal(AgentRunStatus.Succeeded, persisted.Status);
        Assert.Empty(violations);
        Assert.Equal(0, testExit);
        Assert.False(stateLeft);
    }

    private static (CliExecutionAdapter Adapter, string CredentialName) CreateAdapter(string runtime, string stateRoot)
    {
        var cli = new CliRuntimeOptions { StateRoot = stateRoot };
        var secrets = new EnvironmentSecretProvider(NullLogger<EnvironmentSecretProvider>.Instance);
        var runner = new SystemProcessRunner(new CommandPolicyOptions { AllowedCommands = new[] { "claude", "codex" } });

        if (runtime == ClaudeCodeExecutionAdapter.AdapterKey)
        {
            var useOauth = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));
            var variable = useOauth ? "CLAUDE_CODE_OAUTH_TOKEN" : "ANTHROPIC_API_KEY";
            var options = new ClaudeCodeOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("ClaudeCode__ExecutablePath") ?? "claude",
                CredentialSecretName = variable,
                CredentialEnvironmentVariable = variable,
                Models = { [FactoryModel] = Environment.GetEnvironmentVariable("AI_FACTORY_SMOKE_MODEL") ?? "claude-haiku-4-5" }
            };
            return (new ClaudeCodeExecutionAdapter(options, cli, runner, secrets, NullLogger<ClaudeCodeExecutionAdapter>.Instance), variable);
        }

        var codex = new CodexOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("Codex__ExecutablePath") ?? "codex",
            Models = { [FactoryModel] = Environment.GetEnvironmentVariable("AI_FACTORY_SMOKE_MODEL") ?? "gpt-5-codex" }
        };
        return (new CodexExecutionAdapter(codex, cli, runner, secrets, NullLogger<CodexExecutionAdapter>.Instance), codex.CredentialSecretName);
    }

    private string CreateFixtureRepository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        void Write(string relative, string content)
        {
            var path = Path.Combine(repo, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Write(".gitignore", "bin/\nobj/\n");
        Write("Smoke.slnx", "<Solution>\n  <Project Path=\"Calc/Calc.csproj\" />\n  <Project Path=\"Calc.Tests/Calc.Tests.csproj\" />\n</Solution>\n");
        Write("Calc/Calc.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        Write("Calc/Calculator.cs", "namespace Calc;\n\npublic sealed class Calculator\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        Write("Calc.Tests/Calc.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.4\" />\n  </ItemGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Calc/Calc.csproj\" />\n  </ItemGroup>\n</Project>\n");
        Write("Calc.Tests/CalculatorTests.cs", "using Calc;\nusing Xunit;\n\nnamespace Calc.Tests;\n\npublic sealed class CalculatorTests\n{\n    [Fact]\n    public void Add_Works() => Assert.Equal(3, new Calculator().Add(1, 2));\n}\n");
        Run("git", repo, "init", "-q", "-b", "main");
        Run("git", repo, "add", "-A");
        Run("git", repo, "-c", "user.name=smoke", "-c", "user.email=smoke@localhost", "commit", "-q", "-m", "baseline");
        return repo;
    }

    private static (int ExitCode, string Tail) Run(string file, string cwd, params string[] args)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd() + stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        return (process.ExitCode, output.Length <= 600 ? output : output[^600..]);
    }
}
