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
/// Phase 18.10 §22 "End-to-End Scenario" — the full persisted chain: Project
/// Implementation -> (three WorkItems dispatched, executed, and independently
/// verified via the real TaskDispatchScheduler/AgentOrchestrator/
/// AgentRunWorkItemSync/IVerificationGate pipeline, all Completed) -> Project
/// Testing -> deterministic Project verification -> Project Reviewing ->
/// review gate -> Project HumanReview, explicitly proving the Project never
/// becomes Completed on its own. Same real-SQLite-repository conventions as
/// WorkItemExecutionIntegrationTests/RepairVerificationLoopTests.
/// </summary>
public sealed class ProjectCompletionIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    // Phase 18.14 — see WorkItemExecutionIntegrationTests: AgentOrchestrator
    // now resolves a real, existing Project workspace for WorkItem-backed
    // AgentRuns, so the seeded Project needs a real temp directory rather
    // than the non-existent default RootPath.
    private readonly string _workspaceRoot;

    public ProjectCompletionIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-project-completion-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>Deterministic stand-in for the real build/test pipeline, used for both the per-WorkItem and the Project-level gate.</summary>
    private sealed class FakeVerificationGate : IVerificationGate
    {
        private readonly VerificationStatus _status;

        public FakeVerificationGate(VerificationStatus status) => _status = status;

        public int ProjectCallCount { get; private set; }

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(_status, $"fake:{_status}", 0, TimeSpan.Zero));

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default)
        {
            ProjectCallCount++;
            return Task.FromResult(new VerificationResult(_status, $"fake-project:{_status}", 0, TimeSpan.Zero));
        }
    }

    private static TaskDispatchScheduler CreateScheduler(FactoryDbContext db, IVerificationGate projectVerificationGate) => new(
        new WorkItemRepository(db),
        new ProjectRepository(db),
        new TaskDependencyResolver(),
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        new TaskSchedulerOptions { MaxConcurrentTasks = 3 },
        new ExecutionLimitsOptions(),
        new ProjectCompletionGate(
            new ProjectRepository(db), new AgentRunRepository(db), projectVerificationGate,
            NullLogger<ProjectCompletionGate>.Instance),
        new OperationalMetrics(),
        NullLogger<TaskDispatchScheduler>.Instance);

    private static AgentOrchestrator CreateOrchestrator(FactoryDbContext db, IAgentRuntime runtime) => new(
        new AgentRunRepository(db),
        new ProjectRepository(db),
        new FakeAgentToolRegistry(),
        new FakeAgentMemory(),
        runtime,
        new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
        new AgentInstructionBuilder(),
        new StructuredAgentResultParser(),
        NullLogger<AgentOrchestrator>.Instance);

    private static AgentRunWorkItemSync CreateSync(FactoryDbContext db, IVerificationGate workItemVerificationGate) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        workItemVerificationGate, new FakeWorkItemCompletionProcessor(), NullLogger<AgentRunWorkItemSync>.Instance);

    /// <summary>Phase 18.13 — stand-in for the real review/evaluation/learning stage; out of scope for this file's Project-completion-gate integration coverage.</summary>
    private sealed class FakeWorkItemCompletionProcessor : IWorkItemCompletionProcessor
    {
        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
    }

    private async Task<Guid> SeedProjectInImplementationAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var service = new ProjectService(repo);
        var project = await service.CreateAsync("Completion Project", "d");

        project.RootPath = Path.Combine(_workspaceRoot, project.Slug);
        Directory.CreateDirectory(project.RootPath);
        await repo.SaveChangesAsync();

        await service.TransitionStatusAsync(project.Id, ProjectStatus.Analyzing);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Specification);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Architecture);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Planning);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Implementation);
        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync()
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Agent-Implementation", AgentType = "Implementation", SystemPrompt = "x"
        });
    }

    private async Task<Guid> SeedReadyWorkItemAsync(Guid projectId, string title)
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, title, null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        return item.Id;
    }

    [Fact]
    public async Task EndToEnd_AllWorkItemsComplete_ProjectTests_Reviews_AndReachesHumanReview()
    {
        var projectId = await SeedProjectInImplementationAsync();
        await SeedAgentDefinitionAsync();
        var workItemAId = await SeedReadyWorkItemAsync(projectId, "A");
        var workItemBId = await SeedReadyWorkItemAsync(projectId, "B");
        var workItemCId = await SeedReadyWorkItemAsync(projectId, "C");

        var workItemVerificationGate = new FakeVerificationGate(VerificationStatus.Passed);
        var projectVerificationGate = new FakeVerificationGate(VerificationStatus.Passed);

        // ---- Implementation: dispatch and execute all three WorkItems ----
        List<Guid> agentRunIds;
        await using (var db = new FactoryDbContext(_options))
        {
            var cycle = await CreateScheduler(db, projectVerificationGate).RunCycleAsync(projectId);
            Assert.Equal(3, cycle.DispatchedCount);
            agentRunIds = cycle.Outcomes.Select(x => x.AgentRunId!.Value).ToList();
        }

        foreach (var agentRunId in agentRunIds)
        {
            await using var db = new FactoryDbContext(_options);
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult);
            var executed = await CreateOrchestrator(db, runtime).ExecuteAsync(agentRunId);
            Assert.True(executed.Success);

            var completedRun = (await new AgentRunRepository(db).GetByIdAsync(agentRunId))!;
            await CreateSync(db, workItemVerificationGate).SyncAsync(completedRun);
        }

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var items = await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId);
            Assert.All(items, x => Assert.Equal(WorkItemStatus.Completed.ToString(), x.Status));
        }

        // ---- Project completion/review gate: Implementation -> Testing ----
        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db, projectVerificationGate).RunCycleAsync(projectId);
        }

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var project = await new ProjectRepository(verifyDb).GetByIdAsync(projectId);
            Assert.Equal(ProjectStatus.Testing, project!.Status);
        }

        // ---- Testing -> deterministic Project verification -> Reviewing ----
        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db, projectVerificationGate).RunCycleAsync(projectId);
        }

        Assert.Equal(1, projectVerificationGate.ProjectCallCount);

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var project = await new ProjectRepository(verifyDb).GetByIdAsync(projectId);
            Assert.Equal(ProjectStatus.Reviewing, project!.Status);
        }

        // ---- Reviewing -> review gate -> HumanReview ----
        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db, projectVerificationGate).RunCycleAsync(projectId);
        }

        Project finalProject;
        await using (var verifyDb = new FactoryDbContext(_options))
        {
            finalProject = (await new ProjectRepository(verifyDb).GetByIdAsync(projectId))!;
        }

        // The core assertion of this phase: the Project reached HumanReview
        // through the deterministic gate and — despite three successful
        // AgentRuns, three Completed WorkItems, and a passed Project-level
        // verification — never became Completed automatically.
        Assert.Equal(ProjectStatus.HumanReview, finalProject.Status);
        Assert.NotEqual(ProjectStatus.Completed, finalProject.Status);

        // One more cycle must be a safe no-op (idempotent) — HumanReview is
        // not a status this gate acts on; only the explicit human approval
        // action (ProjectService.ApproveAsync) may complete it.
        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db, projectVerificationGate).RunCycleAsync(projectId);
        }

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var project = await new ProjectRepository(verifyDb).GetByIdAsync(projectId);
            Assert.Equal(ProjectStatus.HumanReview, project!.Status);
        }

        // ---- Phase 18.11 — the generic status endpoint can no longer complete a Project ----
        await using (var db = new FactoryDbContext(_options))
        {
            var service = new ProjectService(new ProjectRepository(db));
            await Assert.ThrowsAsync<AI.Factory.Application.Exceptions.ConflictException>(
                () => service.TransitionStatusAsync(projectId, ProjectStatus.Completed));
        }

        // ---- HumanReview -> Completed via the explicit approval boundary ----
        await using (var db = new FactoryDbContext(_options))
        {
            var service = new ProjectService(new ProjectRepository(db));
            var completed = await service.ApproveAsync(projectId, actor: "reviewer@example.com");
            Assert.Equal(ProjectStatus.Completed, completed.Status);
        }
    }
}
