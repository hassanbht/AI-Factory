using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Source;
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
/// Phase 18.18 — Golden End-to-End Project Lifecycle & Repair acceptance test suite.
/// Proves the complete AI Factory product flow as one reproducible, end-to-end Golden Path:
/// Project Creation -> Workspace Provisioning -> Git Source Initialization ->
/// Planning -> WorkItem Creation & Dependencies -> Task Dispatch ->
/// Agent Execution -> Host Workspace Modification -> VerificationGate ->
/// Intentional Defect & Mandatory Repair Loop -> Verification Pass ->
/// Review/Evaluation/Learning -> All WorkItems Completed ->
/// Project Testing -> Project Reviewing -> HumanReview ->
/// Human Approval -> Project Completed.
/// </summary>
public sealed class GoldenProjectLifecycleE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _bareRepoPath;
    private readonly string _projectsBaseDir;
    private readonly IHost _host;

    public GoldenProjectLifecycleE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "golden-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _bareRepoPath = GoldenProjectFixture.CreateBareRepo(_tempDir);
        _projectsBaseDir = Path.Combine(_tempDir, "workspaces");
        Directory.CreateDirectory(_projectsBaseDir);

        var dbPath = Path.Combine(_tempDir, "factory.db");

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        Program.ConfigureServices(builder);

        // Swap SQLite for isolated test DB with dedicated connection pool
        var dbDescriptor = builder.Services.Single(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        builder.Services.Remove(dbDescriptor);
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        // Configure workspace base directory
        var workspaceDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkspaceOptions));
        var workspaceOptions = (AgentWorkspaceOptions)workspaceDescriptor.ImplementationInstance!;
        workspaceOptions.ProjectsBaseDirectory = _projectsBaseDir;
        workspaceOptions.AllowedRoot = _tempDir;

        // Configure scheduler: MaxConcurrentTasks = 1 for deterministic sequential dispatch
        var schedulerDescriptor = builder.Services.Single(d => d.ServiceType == typeof(TaskSchedulerOptions));
        var schedulerOptions = (TaskSchedulerOptions)schedulerDescriptor.ImplementationInstance!;
        schedulerOptions.MaxConcurrentTasks = 1;

        // Register infrastructure dependencies for Project creation and source initialization
        builder.Services.AddScoped<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();
        builder.Services.AddScoped<IProjectSourceInitializer, ProjectSourceInitializer>();
        builder.Services.AddScoped<ProjectService>();
        builder.Services.AddScoped<WorkItemService>();
        builder.Services.AddScoped<TaskDependencyService>();

        // Register the controllable Golden agent runtime for deterministic acceptance
        builder.Services.AddSingleton<GoldenAgentRuntime>();
        var runtimeDescriptor = builder.Services.Single(d => d.ServiceType == typeof(IAgentRuntime));
        builder.Services.Remove(runtimeDescriptor);
        builder.Services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<GoldenAgentRuntime>());

        // Register controllable Golden agent executor for independent review
        var executorDescriptor = builder.Services.Single(d => d.ServiceType == typeof(IAgentExecutor));
        builder.Services.Remove(executorDescriptor);
        builder.Services.AddSingleton<IAgentExecutor, GoldenAgentExecutor>();

        _host = builder.Build();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using var db = new FactoryDbContext(schemaOptions);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public void Dispose()
    {
        _host.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
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

    [Fact]
    public async Task GoldenPath_FullLifecycle_WithDependenciesAndMandatoryRepair_CompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = cts.Token;

        // ──────────────────────────────────────────────────────────────────
        // 1. AgentDefinition Readiness (Section 4)
        // ──────────────────────────────────────────────────────────────────
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db, cancellationToken);
        }

        // ──────────────────────────────────────────────────────────────────
        // 2. Deterministic Project Creation (Section 5)
        // ──────────────────────────────────────────────────────────────────
        Guid projectId;
        string projectSlug;
        string resolvedProjectRoot;

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceResolver>();

            var project = await projectService.CreateAsync("Golden Demo", "Acceptance project", cancellationToken);
            Assert.NotNull(project);
            Assert.Equal(ProjectStatus.Created, project.Status);
            Assert.False(string.IsNullOrWhiteSpace(project.Slug));
            projectId = project.Id;
            projectSlug = project.Slug;

            var workspace = resolver.Resolve(project);
            Assert.True(workspace.IsValid);
            Assert.True(Directory.Exists(workspace.Path));
            resolvedProjectRoot = workspace.Path!;
        }

        // ──────────────────────────────────────────────────────────────────
        // 3. Source Initialization (Section 6)
        // ──────────────────────────────────────────────────────────────────
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var sourceSpec = new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath, "main");

            var initializedProject = await projectService.InitializeSourceAsync(projectId, sourceSpec, cancellationToken);
            Assert.Equal(ProjectStatus.Analyzing, initializedProject.Status);
            Assert.Equal(_bareRepoPath, initializedProject.GitRepository);
            Assert.Equal("main", initializedProject.GitBranch);

            // Verify source files exist in the authoritative workspace
            Assert.True(File.Exists(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs")));
            Assert.True(File.Exists(Path.Combine(resolvedProjectRoot, "tests", "GreetingServiceTests.cs")));
        }

        // ──────────────────────────────────────────────────────────────────
        // 4. Planning & WorkItem Creation with Real Dependencies (Section 3 & 7)
        // ──────────────────────────────────────────────────────────────────
        Guid t01Id, t02Id, t03Id, t04Id;

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var workItemService = scope.ServiceProvider.GetRequiredService<WorkItemService>();
            var dependencyService = scope.ServiceProvider.GetRequiredService<TaskDependencyService>();

            // Advance through lifecycle to Planning
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Specification, cancellationToken);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Architecture, cancellationToken);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Planning, cancellationToken);

            // Create 4 WorkItems:
            // T01 -> baseline/source setup
            // T02 -> implementation change (depends on T01)
            // T03 -> test/change with intentional defect (depends on T02)
            // T04 -> final integration (depends on T03)
            var t01 = await workItemService.CreateAsync(projectId, WorkItemType.Implementation, "T01 - Setup", null, "Normal", cancellationToken);
            var t02 = await workItemService.CreateAsync(projectId, WorkItemType.Implementation, "T02 - Implementation", null, "Normal", cancellationToken);
            var t03 = await workItemService.CreateAsync(projectId, WorkItemType.Implementation, "T03 - Test Verification", null, "Normal", cancellationToken);
            var t04 = await workItemService.CreateAsync(projectId, WorkItemType.Implementation, "T04 - Integration", null, "Normal", cancellationToken);

            t01Id = t01.Id;
            t02Id = t02.Id;
            t03Id = t03.Id;
            t04Id = t04.Id;

            // Wire real dependencies
            await dependencyService.AddDependencyAsync(t02Id, t01Id, cancellationToken);
            await dependencyService.AddDependencyAsync(t03Id, t02Id, cancellationToken);
            await dependencyService.AddDependencyAsync(t04Id, t03Id, cancellationToken);

            // Transition all to Ready
            await workItemService.TransitionStatusAsync(t01Id, WorkItemStatus.Ready, cancellationToken);
            await workItemService.TransitionStatusAsync(t02Id, WorkItemStatus.Ready, cancellationToken);
            await workItemService.TransitionStatusAsync(t03Id, WorkItemStatus.Ready, cancellationToken);
            await workItemService.TransitionStatusAsync(t04Id, WorkItemStatus.Ready, cancellationToken);

            // Advance Project to Implementation
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Implementation, cancellationToken);
        }

        // Configure Golden runtime with workspace root
        var goldenRuntime = _host.Services.GetRequiredService<GoldenAgentRuntime>();
        goldenRuntime.SetWorkspaceRoot(resolvedProjectRoot);

        // ──────────────────────────────────────────────────────────────────
        // 5. WorkItem Dispatch & Worker Execution (Sections 8, 9, 10, 11, 12, 13, 14)
        // ──────────────────────────────────────────────────────────────────
        var agentWorker = GetAgentRunWorker(_host);
        await agentWorker.StartAsync(cancellationToken);

        try
        {
            // --- STEP A: Dispatch T01 ---
            await RunSchedulerCycleAssertDispatchedAsync(projectId, 1);
            var t01Run = await WaitForWorkItemStatusAsync(t01Id, WorkItemStatus.Completed, TimeSpan.FromSeconds(60));
            Assert.Equal(WorkItemStatus.Completed.ToString(), t01Run.Status);

            // --- STEP B: Dispatch T02 (T01 completed, so T02 is now ready) ---
            await RunSchedulerCycleAssertDispatchedAsync(projectId, 1);
            var t02Run = await WaitForWorkItemStatusAsync(t02Id, WorkItemStatus.Completed, TimeSpan.FromSeconds(60));
            Assert.Equal(WorkItemStatus.Completed.ToString(), t02Run.Status);

            // Verify host workspace modification from T02
            var greetingCodeT02 = await File.ReadAllTextAsync(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs"), cancellationToken);
            Assert.Contains("Hello, AI Factory!", greetingCodeT02);

            // --- STEP C: Dispatch T03 (Intentional Defect & Mandatory Repair) ---
            // T03 runs, writes a failing assertion in tests/GreetingServiceTests.cs.
            // VerificationGate runs dotnet test, which fails -> WorkItem transitions to Repairing!
            await RunSchedulerCycleAssertDispatchedAsync(projectId, 1);
            var t03Repairing = await WaitForWorkItemStatusAsync(t03Id, WorkItemStatus.Repairing, TimeSpan.FromSeconds(60));
            Assert.Equal(WorkItemStatus.Repairing.ToString(), t03Repairing.Status);
            Assert.False(string.IsNullOrWhiteSpace(t03Repairing.CurrentError));

            // --- STEP D: Dispatch Repair for T03 ---
            // TaskDispatchScheduler prioritizes Repairing items, increments RepairAttemptCount to 1,
            // dispatches Repair AgentRun, repair agent fixes GreetingService.cs, VerificationGate passes.
            await RunSchedulerCycleAssertDispatchedAsync(projectId, 1);
            var t03Completed = await WaitForWorkItemStatusAsync(t03Id, WorkItemStatus.Completed, TimeSpan.FromSeconds(60));
            Assert.Equal(WorkItemStatus.Completed.ToString(), t03Completed.Status);
            Assert.Equal(1, t03Completed.RepairAttemptCount);

            // Verify repaired source in host workspace
            var greetingCodeRepaired = await File.ReadAllTextAsync(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs"), cancellationToken);
            Assert.Contains("Hello, Repaired AI Factory!", greetingCodeRepaired);

            // --- STEP E: Dispatch T04 (Final Integration) ---
            await RunSchedulerCycleAssertDispatchedAsync(projectId, 1);
            var t04Run = await WaitForWorkItemStatusAsync(t04Id, WorkItemStatus.Completed, TimeSpan.FromSeconds(60));
            Assert.Equal(WorkItemStatus.Completed.ToString(), t04Run.Status);

            // Verify T04 host workspace addition
            var finalCode = await File.ReadAllTextAsync(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs"), cancellationToken);
            Assert.Contains("IsIntegrated", finalCode);
        }
        finally
        {
            await agentWorker.StopAsync(CancellationToken.None);
        }

        // ──────────────────────────────────────────────────────────────────
        // 6. Project-Level Gate: Testing -> Reviewing -> HumanReview (Sections 15, 16, 17)
        // ──────────────────────────────────────────────────────────────────
        // At this point, all 4 WorkItems are Completed.
        // The scheduler cycle triggers ProjectCompletionGate.EvaluateAsync.
        // Implementation -> Testing -> VerifyProjectAsync (dotnet build & test) -> Reviewing -> HumanReview.
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
            var projectRepo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

            // First cycle: Implementation -> Testing
            await scheduler.RunCycleAsync(projectId, cancellationToken);
            var projTesting = (await projectRepo.GetByIdAsync(projectId, cancellationToken))!;
            Assert.Equal(ProjectStatus.Testing, projTesting.Status);

            // Second cycle: Testing -> Reviewing (runs real project-level VerificationGate)
            await scheduler.RunCycleAsync(projectId, cancellationToken);
            var projReviewing = (await projectRepo.GetByIdAsync(projectId, cancellationToken))!;
            Assert.Equal(ProjectStatus.Reviewing, projReviewing.Status);

            // Third cycle: Reviewing -> HumanReview
            await scheduler.RunCycleAsync(projectId, cancellationToken);
            var projHumanReview = (await projectRepo.GetByIdAsync(projectId, cancellationToken))!;
            Assert.Equal(ProjectStatus.HumanReview, projHumanReview.Status);
        }

        // ──────────────────────────────────────────────────────────────────
        // 7. Human Review & Approval Authority (Section 18)
        // ──────────────────────────────────────────────────────────────────
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();

            // Attempting to bypass approval via generic TransitionStatusAsync MUST fail
            await Assert.ThrowsAsync<ConflictException>(() =>
                projectService.TransitionStatusAsync(projectId, ProjectStatus.Completed, cancellationToken));

            // Explicit human approval succeeds
            var completedProject = await projectService.ApproveAsync(projectId, "qa-lead@factory.local", cancellationToken);
            Assert.Equal(ProjectStatus.Completed, completedProject.Status);
        }

        // ──────────────────────────────────────────────────────────────────
        // 8. Final Host Workspace and Artifact Verification (Sections 10, 19)
        // ──────────────────────────────────────────────────────────────────
        Assert.True(File.Exists(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs")));
        Assert.True(File.Exists(Path.Combine(resolvedProjectRoot, "tests", "GreetingServiceTests.cs")));

        var finalServiceFile = await File.ReadAllTextAsync(Path.Combine(resolvedProjectRoot, "src", "GreetingService.cs"), cancellationToken);
        Assert.Contains("Hello, Repaired AI Factory!", finalServiceFile);
        Assert.Contains("IsIntegrated", finalServiceFile);

        // Run independent host dotnet test command to verify the final workspace state compiles and passes
        var testResult = RunDotnetTest(resolvedProjectRoot);
        Assert.True(testResult.Success, $"Final workspace verification failed: {testResult.Output}");
    }

    private async Task RunSchedulerCycleAssertDispatchedAsync(Guid projectId, int expectedDispatched)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
        var result = await scheduler.RunCycleAsync(projectId);
        Assert.Equal(expectedDispatched, result.DispatchedCount);
    }

    private async Task<WorkItem> WaitForWorkItemStatusAsync(Guid workItemId, WorkItemStatus expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IWorkItemRepository>();
            var item = await repo.GetByIdAsync(workItemId);

            if (item is not null && item.Status == expectedStatus.ToString())
            {
                return item;
            }

            await Task.Delay(200);
        }

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWorkItemRepository>();
            var item = await repo.GetByIdAsync(workItemId);
            throw new TimeoutException(
                $"Timed out waiting for WorkItem {workItemId} to reach status {expectedStatus}. Actual status: {item?.Status}, Error: {item?.CurrentError}");
        }
    }

    private static AgentRunWorker GetAgentRunWorker(IHost host)
    {
        return host.Services
            .GetServices<IHostedService>()
            .OfType<AgentRunWorker>()
            .Single();
    }

    private static (bool Success, string Output) RunDotnetTest(string workingDirectory)
    {
        var psi = new ProcessStartInfo("dotnet", "test --no-restore -c Debug")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        var output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
        return (process.ExitCode == 0, output);
    }

    /// <summary>
    /// Deterministic AgentRuntime that carries out real source code modifications
    /// corresponding to the WorkItem objectives in the Golden Path.
    /// </summary>
    public sealed class GoldenAgentRuntime : IAgentRuntime
    {
        private string? _workspaceRoot;

        public void SetWorkspaceRoot(string root) => _workspaceRoot = root;

        public Task<AgentRunExecutionResult> ExecuteAsync(AgentRun agentRun, CancellationToken cancellationToken = default)
        {
            if (_workspaceRoot is null)
            {
                throw new InvalidOperationException("WorkspaceRoot must be configured on GoldenAgentRuntime.");
            }

            var input = agentRun.Input ?? string.Empty;

            if (input.Contains("WorkItem title: T01", StringComparison.OrdinalIgnoreCase))
            {
                // Baseline: no changes needed, existing fixture compiles and tests green
            }
            else if (input.Contains("WorkItem title: T02", StringComparison.OrdinalIgnoreCase))
            {
                // Modify GreetingService.cs to return "Hello, AI Factory!"
                GoldenProjectFixture.ApplyT02ImplementationChange(_workspaceRoot);
            }
            else if (input.Contains("WorkItem title: T03", StringComparison.OrdinalIgnoreCase))
            {
                // Check if this is a repair attempt
                var isRepair = input.Contains("repairing one WorkItem", StringComparison.OrdinalIgnoreCase)
                    || input.Contains("repair attempt", StringComparison.OrdinalIgnoreCase);

                if (!isRepair)
                {
                    // Initial attempt: introduce intentional test defect so VerificationGate fails!
                    GoldenProjectFixture.InjectT03TestDefect(_workspaceRoot);
                }
                else
                {
                    // Repair attempt: fix GreetingService.cs so the test defect expectation is satisfied!
                    GoldenProjectFixture.ApplyT03Repair(_workspaceRoot);
                }
            }
            else if (input.Contains("WorkItem title: T04", StringComparison.OrdinalIgnoreCase))
            {
                // Apply final integration addition
                GoldenProjectFixture.ApplyT04FinalIntegration(_workspaceRoot);
            }

            // Produce valid StructuredAgentResult JSON (Phase 18.1.8 & 18.7 contract)
            var structured = new
            {
                version = 1,
                summary = $"Completed execution for task in run {agentRun.Id}",
                output = "Source modified successfully.",
                data = new
                {
                    execution = new
                    {
                        version = 1,
                        status = "completed",
                        summary = "Task executed and source written successfully."
                    }
                },
                status = "Succeeded"
            };
            var json = JsonSerializer.Serialize(structured);

            var result = new AgentRunExecutionResult(
                Success: true,
                Output: json,
                PromptTokens: 100,
                CompletionTokens: 50,
                TotalTokens: 150,
                DurationMs: 250,
                ArtifactReferences: Array.Empty<string>(),
                ToolCallNames: Array.Empty<string>(),
                ErrorMessage: null,
                FailureCategory: null,
                Provider: "golden-deterministic",
                Model: "golden-acceptance-model");

            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Deterministic AgentExecutor for independent review and evaluation steps
    /// that does not depend on external LLM provider connectivity.
    /// </summary>
    public sealed class GoldenAgentExecutor : IAgentExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var json = """{ "findings": [] }""";
            return Task.FromResult(new AgentExecutionResult(true, json, null, 50, request.WorkingDirectory));
        }
    }
}
