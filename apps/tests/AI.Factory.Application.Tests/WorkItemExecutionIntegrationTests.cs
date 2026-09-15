using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.7 §13 "Application integration tests" — the real chain:
/// TaskDispatchScheduler -> AgentRun -> AgentOrchestrator (FakeAgentRuntime
/// standing in for IAgentRuntime) -> AgentRunWorkItemSync -> WorkItem state,
/// against real SQLite-in-memory repositories. Same pattern as
/// ProjectPlanSchedulerIntegrationTests — no fake scheduler, no fake
/// dependency resolver.
/// </summary>
public sealed class WorkItemExecutionIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    // Phase 18.14 — AgentOrchestrator now resolves a real, existing Project
    // workspace for WorkItem-backed AgentRuns; these tests exercise
    // scheduler/orchestrator/sync wiring (not an actual dotnet build), so
    // each seeded Project gets a real, empty temp directory rather than the
    // non-existent default "projects/{slug}" RootPath.
    private readonly string _workspaceRoot;

    public WorkItemExecutionIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-workitem-execution-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private const string CompletedExecutionResult = """
        {"version":1,"status":"Succeeded","summary":"done","output":"ok","data":{"execution":{"version":1,"status":"completed","summary":"implemented it"}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
        """;

    private static TaskDispatchScheduler CreateScheduler(FactoryDbContext db) => new(
        new WorkItemRepository(db),
        new ProjectRepository(db),
        new TaskDependencyResolver(),
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        new TaskSchedulerOptions { MaxConcurrentTasks = 2 },
        new ExecutionLimitsOptions(),
        new ProjectCompletionGate(
            new ProjectRepository(db), new AgentRunRepository(db), new FakeVerificationGate(VerificationStatus.Passed),
            NullLogger<ProjectCompletionGate>.Instance),
        new OperationalMetrics(),
        NullLogger<TaskDispatchScheduler>.Instance);

    private static AgentOrchestrator CreateOrchestrator(
        FactoryDbContext db, IAgentRuntime runtime, AgentWorkspaceOptions? workspaceOptions = null) => new(
        new AgentRunRepository(db),
        new ProjectRepository(db),
        new FakeAgentToolRegistry(),
        new FakeAgentMemory(),
        runtime,
        new ProjectWorkspaceResolver(workspaceOptions ?? new AgentWorkspaceOptions()),
        new AgentInstructionBuilder(),
        new StructuredAgentResultParser(),
        NullLogger<AgentOrchestrator>.Instance);

    private static AgentRunWorkItemSync CreateSync(FactoryDbContext db, IVerificationGate? verificationGate = null) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        verificationGate ?? new FakeVerificationGate(VerificationStatus.Passed),
        new FakeWorkItemCompletionProcessor(),
        NullLogger<AgentRunWorkItemSync>.Instance);

    /// <summary>
    /// Phase 18.13 — stand-in for the real review/evaluation/learning stage;
    /// this file exercises scheduler/orchestrator/sync wiring, not
    /// WorkItemCompletionProcessor's own review/evaluation internals
    /// (covered separately by WorkItemCompletionProcessorTests).
    /// </summary>
    private sealed class FakeWorkItemCompletionProcessor : IWorkItemCompletionProcessor
    {
        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
    }

    /// <summary>Deterministic stand-in for the real build/test pipeline — these tests exercise
    /// scheduler/orchestrator/sync wiring, not an actual dotnet build against a seeded Project's
    /// (non-existent) RootPath.</summary>
    private sealed class FakeVerificationGate : IVerificationGate
    {
        private readonly VerificationStatus _status;

        public FakeVerificationGate(VerificationStatus status)
        {
            _status = status;
        }

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, "fake", 0, TimeSpan.Zero));

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, "fake", 0, TimeSpan.Zero));
    }

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Execution Project", "d");

        project.RootPath = Path.Combine(_workspaceRoot, project.Slug);
        Directory.CreateDirectory(project.RootPath);
        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file exercises scheduler/orchestrator
        // wiring, not source initialization itself.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();

        return project.Id;
    }

    /// <summary>
    /// Phase 18.15 — unlike <see cref="SeedProjectAsync"/>, this never manually
    /// touches Project.RootPath or the filesystem: it goes through the real
    /// ProjectService + ProjectWorkspaceProvisioner, exactly as
    /// POST /api/projects does, proving a freshly created Project's workspace
    /// is usable by the existing dispatch/orchestration pipeline without any
    /// test-only workaround. No AllowedRoot here: Project's default RootPath
    /// ("projects/{slug}") is relative, and the (unchanged, Phase 18.14)
    /// resolver treats a relative RootPath as CWD-relative before applying
    /// any AllowedRoot boundary, so this test uses the same unset-AllowedRoot
    /// trust model as CreateOrchestrator's own default (see final report for
    /// the AllowedRoot + relative-RootPath limitation this surfaced).
    /// </summary>
    private async Task<Guid> SeedProvisionedProjectAsync()
    {
        var options = new AgentWorkspaceOptions();

        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var provisioner = new ProjectWorkspaceProvisioner(
            new ProjectWorkspaceResolver(options), options, NullLogger<ProjectWorkspaceProvisioner>.Instance);

        var project = await new ProjectService(repo, provisioner).CreateAsync("Provisioned Execution Project", "d");

        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file exercises scheduler/orchestrator
        // wiring, not source initialization itself.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();

        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType = "Implementation")
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}", AgentType = agentType, SystemPrompt = "x"
        });
    }

    private async Task<Guid> SeedReadyWorkItemAsync(Guid projectId, string title = "A")
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, title, null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        return item.Id;
    }

    [Fact]
    public async Task Dispatch_ThenSuccessfulExecution_LinksAgentRun_PersistsStructuredResult_CompletesWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var workItemId = await SeedReadyWorkItemAsync(projectId);

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            Assert.Equal(1, cycle.DispatchedCount);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;
        }

        // §13 items 1/2 — Ready WorkItem was dispatched, AgentRun is linked.
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var run = await new AgentRunRepository(verifyDb).GetByIdAsync(agentRunId);
            Assert.Equal(projectId, run!.ProjectId);
            Assert.Equal(workItemId, run.WorkItemId);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult);
            var result = await CreateOrchestrator(db, runtime).ExecuteAsync(agentRunId);
            Assert.True(result.Success);
        }

        // §13 item 4 — structured result persisted.
        AgentRun completedRun;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            completedRun = (await new AgentRunRepository(verifyDb).GetByIdAsync(agentRunId))!;
            Assert.NotNull(completedRun.StructuredResult);
            Assert.Equal(StructuredAgentResult.CurrentVersion, completedRun.StructuredResultVersion);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateSync(db).SyncAsync(completedRun);
        }

        // §13 item 5 — WorkItem transitions per the deterministic policy.
        await using var finalDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(finalDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task InvalidExecutionResult_ProducesNoUnsafeStateMutation()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var workItemId = await SeedReadyWorkItemAsync(projectId);

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            agentRunId = (await CreateScheduler(db).RunCycleAsync(projectId)).Outcomes.Single().AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            // §13 item 6 — the agent's raw output is not JSON at all, so the
            // structured-result parser marks it Invalid — an untrustworthy report.
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, "I did some stuff, looks good!");
            await CreateOrchestrator(db, runtime).ExecuteAsync(agentRunId);
        }

        AgentRun completedRun;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            completedRun = (await new AgentRunRepository(verifyDb).GetByIdAsync(agentRunId))!;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateSync(db).SyncAsync(completedRun);
        }

        await using var finalDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(finalDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(workItem.CurrentError));
    }

    [Fact]
    public async Task RuntimeFailure_ProducesDeterministicWorkItemFailure()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var workItemId = await SeedReadyWorkItemAsync(projectId);

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            agentRunId = (await CreateScheduler(db).RunCycleAsync(projectId)).Outcomes.Single().AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            // §13 item 7 — runtime-level failure, not an execution-result question at all.
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Failure)).ExecuteAsync(agentRunId);
        }

        AgentRun completedRun;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            completedRun = (await new AgentRunRepository(verifyDb).GetByIdAsync(agentRunId))!;
            Assert.Equal(AgentRunStatus.Failed, completedRun.Status);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateSync(db).SyncAsync(completedRun);
        }

        await using var finalDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(finalDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task DuplicateTerminalResultProcessing_IsSafe()
    {
        // §13 item 8 / §11 idempotency — observing the same terminal AgentRun
        // twice must not create another WorkItem/AgentRun, regress the
        // WorkItem, or throw.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var workItemId = await SeedReadyWorkItemAsync(projectId);

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            agentRunId = (await CreateScheduler(db).RunCycleAsync(projectId)).Outcomes.Single().AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult);
            await CreateOrchestrator(db, runtime).ExecuteAsync(agentRunId);
        }

        AgentRun completedRun;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            completedRun = (await new AgentRunRepository(verifyDb).GetByIdAsync(agentRunId))!;
        }

        await using (var db1 = new FactoryDbContext(_options))
        {
            await CreateSync(db1).SyncAsync(completedRun);
        }

        // Second, independent observation of the exact same terminal AgentRun.
        await using (var db2 = new FactoryDbContext(_options))
        {
            await CreateSync(db2).SyncAsync(completedRun);
        }

        await using var finalDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(finalDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
        Assert.Equal(1, await finalDb.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
        Assert.Single(await new WorkItemRepository(finalDb).GetByProjectIdAsync(projectId));
    }

    [Fact]
    public async Task DependentWorkItem_BecomesDispatchable_OnlyAfterDependencyCompletes()
    {
        // §13 item 9 — existing dependency/readiness behavior remains
        // correct: TaskDependencyResolver only treats Completed as
        // satisfying a dependency, unmodified.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var rootId = await SeedReadyWorkItemAsync(projectId, "Root");
        var dependentId = await SeedReadyWorkItemAsync(projectId, "Dependent");

        await using (var db = new FactoryDbContext(_options))
        {
            await new TaskDependencyService(new WorkItemRepository(db), new TaskDependencyResolver())
                .AddDependencyAsync(dependentId, rootId);
        }

        Guid rootRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
            Assert.Equal(1, cycle.DispatchedCount);
            rootRunId = cycle.Outcomes.Single(o => o.Dispatched).AgentRunId!.Value;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult);
            await CreateOrchestrator(db, runtime).ExecuteAsync(rootRunId);
        }

        AgentRun completedRootRun;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            completedRootRun = (await new AgentRunRepository(verifyDb).GetByIdAsync(rootRunId))!;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateSync(db).SyncAsync(completedRootRun);
        }

        await using var schedulerDb = new FactoryDbContext(_options);
        var secondCycle = await CreateScheduler(schedulerDb).RunCycleAsync(projectId);

        Assert.Equal(1, secondCycle.DispatchedCount);
        Assert.Equal(dependentId, secondCycle.Outcomes.Single(o => o.Dispatched).WorkItemId);
    }

    /// <summary>
    /// Phase 18.15 §10/§15/§23 — the practical end-to-end gap this phase
    /// closes: a Project created through the real ProjectService (no manual
    /// RootPath/directory setup) reaches WorkItem dispatch and
    /// AgentOrchestrator resolves its workspace successfully, because
    /// ProjectService.CreateAsync provisioned Project.RootPath before
    /// persisting. Before Phase 18.15 this failed deterministically at
    /// workspace resolution (Phase 18.14 report).
    /// </summary>
    [Fact]
    public async Task ProvisionedProject_WorkItemDispatch_AgentOrchestratorResolvesWorkspace()
    {
        var workspaceOptions = new AgentWorkspaceOptions();
        var projectId = await SeedProvisionedProjectAsync();
        await SeedAgentDefinitionAsync();
        var workItemId = await SeedReadyWorkItemAsync(projectId);

        string? resolvedPath = null;

        try
        {
            await using (var verifyDb = new FactoryDbContext(_options))
            {
                var project = (await new ProjectRepository(verifyDb).GetByIdAsync(projectId))!;
                var resolution = new ProjectWorkspaceResolver(workspaceOptions).Resolve(project);
                Assert.True(resolution.IsValid, resolution.FailureReason);
                Assert.True(Directory.Exists(resolution.Path));
                resolvedPath = resolution.Path;

                // §11/§15 verification-compatibility — VerificationGate/BuildExecutor/
                // TestExecutor consume Project.RootPath directly (no second resolved
                // path); with no AllowedRoot configured, GetFullPath(Project.RootPath)
                // is exactly what both the Agent's resolver and a `dotnet build`/`dotnet
                // test` invocation against that same relative path resolve to — the
                // exact same directory.
                Assert.Equal(resolution.Path, Path.GetFullPath(project.RootPath));
            }

            Guid agentRunId;
            await using (var db = new FactoryDbContext(_options))
            {
                var cycle = await CreateScheduler(db).RunCycleAsync(projectId);
                Assert.Equal(1, cycle.DispatchedCount);
                agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;
            }

            await using (var db = new FactoryDbContext(_options))
            {
                var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult);
                var result = await CreateOrchestrator(db, runtime, workspaceOptions).ExecuteAsync(agentRunId);
                Assert.True(result.Success);
            }

            await using var finalDb = new FactoryDbContext(_options);
            var run = await new AgentRunRepository(finalDb).GetByIdAsync(agentRunId);
            Assert.Equal(AgentRunStatus.Succeeded, run!.Status);
        }
        finally
        {
            if (resolvedPath is not null && Directory.Exists(resolvedPath))
            {
                Directory.Delete(resolvedPath, recursive: true);
            }
        }
    }
}
