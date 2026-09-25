using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Source;
using AI.Factory.Infrastructure.Validation;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase E — Real OpenHands Agent Server E2E Tests.
/// Exercises the real OpenHandsExecutionAdapter through the real AI Factory Worker pipeline:
/// Ready WorkItem / Queued AgentRun -> AgentRunWorker -> AgentOrchestrator -> AdapterDispatchingAgentRuntime
/// -> OpenHandsExecutionAdapter -> Docker OpenHands Agent Server container -> CodeActAgent -> OpenRouter
/// -> ExecutionOutcome -> AgentRun persisted -> Factory verification / acceptance -> 0 orphan containers.
/// Opt-in only via AI_FACTORY_OPENHANDS_SMOKE_TEST=true or AI_FACTORY_HERMES_SMOKE_TEST=true.
/// </summary>
public sealed class RealOpenHandsE2ETests
{
    private const string SmokeMarker = "AI_FACTORY_OPENHANDS_SMOKE_E2E_OK";
    private const string SmokePrompt =
        $"Respond with a one-line summary containing exactly this marker string: {SmokeMarker}. Do not emit any other explanation.";

    private static string OpenHandsModel =>
        Environment.GetEnvironmentVariable("AI_FACTORY_OPENHANDS_MODEL")
        ?? Environment.GetEnvironmentVariable("AI_FACTORY_LIVE_MODEL")
        ?? "deepseek/deepseek-v4-flash-0731";

    private static bool Enabled =>
        (bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_OPENHANDS_SMOKE_TEST"), out var enabled) && enabled)
        || (bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var hermesEnabled) && hermesEnabled);

    [Fact]
    public async Task Smoke_RealOpenHandsAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();

        var before = testHost.Metrics.GetSnapshot();

        var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        Assert.NotNull(final.Output);
        Assert.Contains(SmokeMarker, final.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(final.StartedAtUtc);
        Assert.NotNull(final.CompletedAtUtc);

        var after = testHost.Metrics.GetSnapshot();
        Assert.Equal(before.AgentRunsCreated + 1, after.AgentRunsCreated);
        Assert.Equal(before.AgentRunsStarted + 1, after.AgentRunsStarted);
        Assert.Equal(before.AgentRunsSucceeded + 1, after.AgentRunsSucceeded);
        Assert.Equal(before.AgentWorkerClaimSuccess + 1, after.AgentWorkerClaimSuccess);

        // Logging / Correlation check (§50)
        var runIdText = run.Id.ToString();
        Assert.Contains(testHost.Logs, l => l.Contains("claimed by worker") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRunStarted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("OpenHandsRuntimeStarted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("OpenHandsRuntimeCompleted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRunCompleted") && l.Contains(runIdText));

        // Result metadata check (§26)
        var (adapter, exitReason) = ReadAdapterMetadata(final.StructuredResult);
        Assert.Equal("OpenHands", adapter);
        Assert.Equal(OpenHandsExecutionAdapter.ExitReasons.Completed, exitReason);
    }

    [Fact]
    public async Task StructuredResult_RealOpenHandsAgentRun_ThroughFullFactoryPipeline_ReachesValidStructuredResult()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();

        const string marker = "AI_FACTORY_OPENHANDS_STRUCTURED_OK";
        var (run, _) = await CreateQueuedRunAsync(
            testHost, $"Respond with a one-line summary containing exactly this marker: {marker}");

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        Assert.Equal(StructuredAgentResult.CurrentVersion, final.StructuredResultVersion);
        Assert.NotNull(final.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            final.StructuredResult!, StructuredAgentResult.JsonOptions);

        Assert.NotNull(structured);
        Assert.NotEqual(StructuredResultStatus.Invalid, structured!.Status);

        var haystack = $"{structured.Summary} {structured.Output} {structured.Data}";
        Assert.Contains(marker, haystack, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkItemExecution_RealOpenHands_ModifiesProjectWorkspace_AndPassesFactoryVerification()
    {
        if (!Enabled)
        {
            return;
        }

        // 1. Build fixture: bare Git repo with a minimal .NET project
        var fixtureTemp = Path.Combine(Path.GetTempPath(), "aif-openhands-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureTemp);

        string bareRepoPath;
        try
        {
            bareRepoPath = CreateDemoFixtureBareRepo(fixtureTemp);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to create fixture bare repo: " + ex.Message, ex);
        }

        var projectsBase = Path.Combine(fixtureTemp, "projects");
        Directory.CreateDirectory(projectsBase);

        var workspaceOptions = new AgentWorkspaceOptions
        {
            ProjectsBaseDirectory = projectsBase,
            AllowedRoot = fixtureTemp
        };
        var resolver = new ProjectWorkspaceResolver(workspaceOptions);
        var commandPolicyOptions = new CommandPolicyOptions();
        var executionLimitsOptions = new ExecutionLimitsOptions { BuildTimeoutMinutes = 5 };

        using var testHost = BuildHost(
            configureWorker: o => o.ExecutionTimeout = TimeSpan.FromSeconds(300),
            configureWorkspace: o =>
            {
                o.ProjectsBaseDirectory = projectsBase;
                o.AllowedRoot = fixtureTemp;
            });

        Guid projectId, workItemId, agentRunId;
        string resolvedProjectRoot;

        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            var provisioner = new ProjectWorkspaceProvisioner(
                resolver, workspaceOptions,
                NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var sourceInitializer = new ProjectSourceInitializer(
                resolver, new SystemProcessRunner(commandPolicyOptions),
                executionLimitsOptions,
                NullLogger<ProjectSourceInitializer>.Instance);
            var projectService = new ProjectService(
                new ProjectRepository(db), provisioner, sourceInitializer: sourceInitializer);

            var project = await projectService.CreateAsync("OpenHands Workspace Modifier", "E2E description");
            projectId = project.Id;

            resolvedProjectRoot = resolver.Resolve(project).Path!;

            // Clone local bare repo into workspace
            await projectService.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, bareRepoPath));

            var markerFile = Path.Combine(resolvedProjectRoot, "src", "Marker.cs");
            Assert.True(File.Exists(markerFile), $"Fixture file missing: {markerFile}");
            Assert.Contains("MARKER_BEFORE", await File.ReadAllTextAsync(markerFile));

            // Create and dispatch WorkItem
            await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "Coder", AgentType = "Implementation" });

            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation,
                "Replace marker string in src/Marker.cs",
                "In src/Marker.cs, find the line containing \"MARKER_BEFORE\" and change it to \"MARKER_AFTER\". " +
                "Do not modify any other file. Run dotnet test to confirm the project still builds.",
                "Normal");
            workItemId = item.Id;
            await workItems.TransitionStatusAsync(workItemId, WorkItemStatus.Ready);

            var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
            var cycle = await scheduler.RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;

            var runRepo = new AgentRunRepository(db);
            var reloaded = (await runRepo.GetByIdAsync(agentRunId))!;
            reloaded.Model = OpenHandsModel;
            await runRepo.UpdateAsync(reloaded);
        }

        // Run worker
        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var finalRun = await WaitForTerminalAsync(testHost, agentRunId, TimeSpan.FromSeconds(300));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(finalRun);
        Assert.Equal(AgentRunStatus.Succeeded, finalRun!.Status);
        var (adapter, _) = ReadAdapterMetadata(finalRun.StructuredResult);
        Assert.Equal("OpenHands", adapter);

        // Verify host filesystem modification
        var modifiedFile = Path.Combine(resolvedProjectRoot, "src", "Marker.cs");
        Assert.True(File.Exists(modifiedFile), $"src/Marker.cs not found in workspace '{resolvedProjectRoot}'");
        var content = await File.ReadAllTextAsync(modifiedFile);
        Assert.True(content.Contains("MARKER_AFTER"),
            $"Agent did not replace MARKER_BEFORE with MARKER_AFTER in src/Marker.cs. Actual: {content}");

        // Factory Verification Gate executes build + test
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var gate = scope.ServiceProvider.GetRequiredService<IVerificationGate>();
            var projectRepo = new ProjectRepository(db);
            var project = (await projectRepo.GetByIdAsync(projectId))!;
            var workItemRepo = new WorkItemRepository(db);
            var workItem = (await workItemRepo.GetByIdAsync(workItemId))!;

            var verification = await gate.VerifyAsync(project, workItem, CancellationToken.None);

            Assert.True(verification.Status == VerificationStatus.Passed,
                $"VerificationGate failed after agent modification. Summary: {verification.Summary}");
        }

        // WorkItem leaves Running and reaches Completed
        var finalWorkItem = await WaitForWorkItemLeavesRunningAsync(testHost, workItemId, TimeSpan.FromSeconds(30));
        Assert.NotNull(finalWorkItem);
        Assert.True(finalWorkItem!.Status == WorkItemStatus.Completed.ToString(),
            $"WorkItem did not reach Completed — actual: {finalWorkItem.Status}");

        try { Directory.Delete(fixtureTemp, recursive: true); } catch { }
    }

    private static async Task<(AgentRun Run, Guid DefinitionId)> CreateQueuedRunAsync(
        TestHost testHost, string input, Guid? projectId = null, Guid? agentDefinitionId = null)
    {
        await using var scope = testHost.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var runRepository = new AgentRunRepository(db);
        var definitionRepository = new AgentDefinitionRepository(db);

        var definitionId = agentDefinitionId;
        if (definitionId is null)
        {
            var definition = await definitionRepository.CreateAsync(new AgentDefinition { Name = "Coder", AgentType = "Coding" });
            definitionId = definition.Id;
        }

        var service = new AgentRunService(
            runRepository, definitionRepository, testHost.Metrics, NullLogger<AgentRunService>.Instance);

        var run = await service.CreateAsync(new CreateAgentRunCommand(definitionId.Value, projectId, input));
        run.Model = OpenHandsModel;
        await runRepository.UpdateAsync(run);

        return (run, definitionId.Value);
    }

    private static async Task<AgentRun?> WaitForTerminalAsync(TestHost testHost, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var run = await new AgentRunRepository(db).GetByIdAsync(runId);

            if (run is not null && AgentRunTransitions.IsTerminal(run.Status))
            {
                return run;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task<WorkItem?> WaitForWorkItemLeavesRunningAsync(TestHost testHost, Guid workItemId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var item = await new WorkItemRepository(db).GetByIdAsync(workItemId);

            if (item is not null && item.Status != WorkItemStatus.Running.ToString())
            {
                return item;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static AI.Factory.Worker.AgentRunWorker GetAgentRunWorker(IHost host) =>
        (AI.Factory.Worker.AgentRunWorker)host.Services
            .GetServices<IHostedService>()
            .Single(s => s is AI.Factory.Worker.AgentRunWorker);

    private static TestHost BuildHost(
        Action<OpenHandsOptions>? configureOpenHands = null,
        Action<ExecutionOptions>? configureExecution = null,
        Action<AgentWorkerOptions>? configureWorker = null,
        Action<AgentWorkspaceOptions>? configureWorkspace = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var logs = new List<string>();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        builder.Logging.AddProvider(new CapturingLoggerProvider(logs));

        Program.ConfigureServices(builder);

        var dbDescriptor = builder.Services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        if (dbDescriptor is not null)
        {
            builder.Services.Remove(dbDescriptor);
        }
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(connection));

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-e2e-openhands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        // Configure OpenHands options
        var openHandsDescriptor = builder.Services.Single(d => d.ServiceType == typeof(OpenHandsOptions));
        var openHandsOptions = (OpenHandsOptions)openHandsDescriptor.ImplementationInstance!;
        openHandsOptions.WorkspaceRoot = workspaceRoot;
        openHandsOptions.NetworkMode = "bridge";
        openHandsOptions.DefaultModel = OpenHandsModel;
        openHandsOptions.ProviderBaseUrl = "https://openrouter.ai/api/v1";
        openHandsOptions.ProviderApiKeySecretName = LiveWorkerHost.LiveApiKeySecretName;
        openHandsOptions.ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
        configureOpenHands?.Invoke(openHandsOptions);

        // Configure ExecutionOptions to route Coding and default to OpenHands
        var executionDescriptor = builder.Services.Single(d => d.ServiceType == typeof(ExecutionOptions));
        var executionOptions = (ExecutionOptions)executionDescriptor.ImplementationInstance!;
        executionOptions.Profiles["OpenHands"] = new ExecutionProfileOptions { Adapter = "OpenHands" };
        executionOptions.DefaultProfile = "OpenHands";
        executionOptions.AgentTypeProfiles["Coding"] = "OpenHands";
        executionOptions.AgentTypeProfiles["Implementation"] = "OpenHands";
        configureExecution?.Invoke(executionOptions);

        var workerOptions = new AgentWorkerOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(300),
            ExecutionTimeout = TimeSpan.FromSeconds(300)
        };
        configureWorker?.Invoke(workerOptions);

        var workerOptionsDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkerOptions));
        builder.Services.Remove(workerOptionsDescriptor);
        builder.Services.AddSingleton(workerOptions);

        if (configureWorkspace is not null)
        {
            var workspaceDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkspaceOptions));
            var existingOptions = (AgentWorkspaceOptions)workspaceDescriptor.ImplementationInstance!;
            configureWorkspace(existingOptions);
        }

        var host = builder.Build();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        using (var db = new FactoryDbContext(schemaOptions))
        {
            db.Database.EnsureCreated();
        }

        var metrics = host.Services.GetRequiredService<IOperationalMetrics>();

        return new TestHost(host, connection, openHandsOptions, metrics, logs, workspaceRoot);
    }

    private static string CreateDemoFixtureBareRepo(string tempDir)
    {
        var seedDir = Path.Combine(tempDir, "seed");
        Directory.CreateDirectory(seedDir);

        var srcDir = Path.Combine(seedDir, "src");
        var testsDir = Path.Combine(seedDir, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(Path.Combine(seedDir, "Demo.slnx"), """
            <Solution>
              <Project Path="src/Demo.csproj" />
              <Project Path="tests/Demo.Tests.csproj" />
            </Solution>
            """);

        File.WriteAllText(Path.Combine(srcDir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(srcDir, "Marker.cs"), """
            namespace Demo;

            public static class Marker
            {
                public const string Value = "MARKER_BEFORE";
            }
            """);

        File.WriteAllText(Path.Combine(testsDir, "Demo.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2">
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <ProjectReference Include="../src/Demo.csproj" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(testsDir, "MarkerTests.cs"), """
            using Xunit;

            namespace Demo.Tests;

            public class MarkerTests
            {
                [Fact]
                public void Marker_Value_IsNotEmpty()
                {
                    Assert.NotEmpty(Demo.Marker.Value);
                }
            }
            """);

        RunGitInFixture(seedDir, "init", "-b", "main");
        RunGitInFixture(seedDir, "config", "user.email", "test@example.com");
        RunGitInFixture(seedDir, "config", "user.name", "Test");
        RunGitInFixture(seedDir, "config", "commit.gpgsign", "false");
        RunGitInFixture(seedDir, "add", ".");
        RunGitInFixture(seedDir, "commit", "-m", "initial commit");

        var bareRepoPath = Path.Combine(tempDir, "demo.git");
        RunGitInFixture(tempDir, "clone", "--bare", seedDir, bareRepoPath);

        return bareRepoPath;
    }

    private static void RunGitInFixture(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }

    private sealed class TestHost : IDisposable
    {
        public TestHost(
            IHost host, SqliteConnection connection, OpenHandsOptions openHands, IOperationalMetrics metrics,
            List<string> logs, string workspaceRoot)
        {
            Host = host;
            Connection = connection;
            OpenHands = openHands;
            Metrics = metrics;
            Logs = logs;
            _workspaceRoot = workspaceRoot;
        }

        private readonly string _workspaceRoot;

        public IHost Host { get; }
        public SqliteConnection Connection { get; }
        public OpenHandsOptions OpenHands { get; }
        public IOperationalMetrics Metrics { get; }
        public List<string> Logs { get; }

        public void Dispose()
        {
            Host.Dispose();
            Connection.Dispose();

            try
            {
                if (Directory.Exists(_workspaceRoot))
                {
                    Directory.Delete(_workspaceRoot, recursive: true);
                }
            }
            catch { }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines;

        public CapturingLoggerProvider(List<string> lines) => _lines = lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _lines;

            public CapturingLogger(string category, List<string> lines)
            {
                _category = category;
                _lines = lines;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = $"[{_category}] {formatter(state, exception)}";
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    private static (string? Adapter, string? ExitReason) ReadAdapterMetadata(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult))
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(structuredResult);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "Metadata", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? adapter = null;
            string? exitReason = null;

            foreach (var entry in property.Value.EnumerateObject())
            {
                if (string.Equals(entry.Name, "ExecutionAdapter", StringComparison.OrdinalIgnoreCase))
                {
                    adapter = entry.Value.ToString();
                }
                else if (string.Equals(entry.Name, "ExitReason", StringComparison.OrdinalIgnoreCase))
                {
                    exitReason = entry.Value.ToString();
                }
            }

            return (adapter, exitReason);
        }

        return (null, null);
    }
}
