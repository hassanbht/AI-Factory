using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.6 §27 "Planner execution", "Deterministic plan integration", and
/// "Idempotency/concurrency" coverage. Drives the real chain — ProjectPlanner
/// -> AgentOrchestrator (FakeAgentRuntime standing in for IAgentRuntime) ->
/// ProjectPlanner.MaterializeAsync -> ProjectPlanService -> WorkItems ->
/// TaskDispatchScheduler — against real SQLite-in-memory repositories, same
/// pattern as ProjectPlanSchedulerIntegrationTests.
/// </summary>
public sealed class ProjectPlannerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectPlannerServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private const string ValidCandidateJson = """
        {"version":1,"status":"Succeeded","summary":"plan ready","output":"ok","data":{"plan":{"version":1,"summary":"s","tasks":[
          {"planKey":"root","title":"Root task","type":"Implementation","priority":"Normal","dependsOn":[]},
          {"planKey":"dependent","title":"Dependent task","type":"Test","priority":"Normal","dependsOn":["root"]}
        ]}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
        """;

    private const string UnknownDependencyCandidateJson = """
        {"version":1,"status":"Succeeded","summary":"plan","output":"ok","data":{"plan":{"version":1,"tasks":[
          {"planKey":"a","title":"A","type":"Implementation","priority":"Normal","dependsOn":["ghost"]}
        ]}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
        """;

    private static ProjectPlanService CreatePlanService(FactoryDbContext db) => new(
        new ProjectRepository(db),
        new WorkItemRepository(db),
        new TaskDependencyResolver(),
        new AgentDefinitionRepository(db),
        new DatabaseTransactionManager(db, new OperationalMetrics(), NullLogger<DatabaseTransactionManager>.Instance),
        NullLogger<ProjectPlanService>.Instance);

    private static ProjectPlanner CreatePlanner(FactoryDbContext db, PlannerOptions? options = null) => new(
        new ProjectRepository(db),
        new AgentDefinitionRepository(db),
        new AgentRunRepository(db),
        new AgentRunService(
            new AgentRunRepository(db), new AgentDefinitionRepository(db), new OperationalMetrics(), NullLogger<AgentRunService>.Instance),
        CreatePlanService(db),
        new PlannerResultMapper(),
        options ?? new PlannerOptions(),
        NullLogger<ProjectPlanner>.Instance);

    private static AgentOrchestrator CreateOrchestrator(FactoryDbContext db, IAgentRuntime runtime) => new(
        new AgentRunRepository(db),
        new FakeAgentToolRegistry(),
        new FakeAgentMemory(),
        runtime,
        new AgentInstructionBuilder(),
        new StructuredAgentResultParser(),
        NullLogger<AgentOrchestrator>.Instance);

    private static TaskDispatchScheduler CreateScheduler(FactoryDbContext db) => new(
        new WorkItemRepository(db),
        new ProjectRepository(db),
        new TaskDependencyResolver(),
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        new TaskSchedulerOptions { MaxConcurrentTasks = 2 },
        new OperationalMetrics(),
        NullLogger<TaskDispatchScheduler>.Instance);

    private async Task<Guid> SeedPlanningProjectAsync(string name = "Planner Project")
    {
        await using var db = new FactoryDbContext(_options);
        var service = new ProjectService(new ProjectRepository(db));
        var project = await service.CreateAsync(name, "d");
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Analyzing);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Specification);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Architecture);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Planning);
        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}",
            AgentType = agentType,
            SystemPrompt = "x"
        });
    }

    // ---------------- Planner execution (§27 items 8-11) ----------------

    [Fact]
    public async Task GenerateAsync_CreatesProjectLevelAgentRun_WithNoWorkItemId()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        var run = await CreatePlanner(db).GenerateAsync(projectId);

        Assert.Equal(projectId, run.ProjectId);
        Assert.Null(run.WorkItemId);
        Assert.Equal(AgentRunStatus.Queued, run.Status);
    }

    [Fact]
    public async Task SuccessfulExecution_PersistsStructuredResultOnAgentRun()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);
        await SeedAgentDefinitionAsync("Implementation");
        await SeedAgentDefinitionAsync("Test");

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            var run = await CreatePlanner(db).GenerateAsync(projectId);
            runId = run.Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var runtime = new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, ValidCandidateJson);
            var result = await CreateOrchestrator(db, runtime).ExecuteAsync(runId);
            Assert.True(result.Success);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var persisted = await new AgentRunRepository(verifyDb).GetByIdAsync(runId);
        Assert.Equal(AgentRunStatus.Succeeded, persisted!.Status);
        Assert.NotNull(persisted.StructuredResult);
        Assert.Equal(StructuredAgentResult.CurrentVersion, persisted.StructuredResultVersion);
    }

    // ---------------- Deterministic plan integration (§27 items 15-20) ----------------

    [Fact]
    public async Task Materialize_ValidCandidate_PersistsWorkItemsAndDependencies_SchedulerEligible()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);
        await SeedAgentDefinitionAsync("Implementation");
        await SeedAgentDefinitionAsync("Test");

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, ValidCandidateJson)).ExecuteAsync(runId);
        }

        PlannerMaterializationResult materialization;
        await using (var db = new FactoryDbContext(_options))
        {
            materialization = await CreatePlanner(db).MaterializeAsync(runId);
        }

        Assert.Equal(PlannerMaterializationOutcome.Succeeded, materialization.Outcome);
        Assert.NotNull(materialization.Plan);
        Assert.Equal(2, materialization.Plan!.Tasks.Count);

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var items = await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId);
            Assert.Equal(2, items.Count);
            var dependent = items.Single(x => x.PlanKey == "dependent");
            Assert.Single(dependent.Dependencies);
            Assert.Equal(items.Single(x => x.PlanKey == "root").Id, dependent.Dependencies.Single().DependsOnWorkItemId);
        }

        // §27 items 19/20 — root is dispatched, dependent stays blocked.
        await using var schedulerDb = new FactoryDbContext(_options);
        var cycle = await CreateScheduler(schedulerDb).RunCycleAsync(projectId);
        Assert.Equal(1, cycle.DispatchedCount);
    }

    [Fact]
    public async Task Materialize_CandidateWithUnknownDependency_IsPlanValidationFailed_NoPartialWorkItems()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);
        await SeedAgentDefinitionAsync("Implementation");

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, UnknownDependencyCandidateJson))
                .ExecuteAsync(runId);
        }

        PlannerMaterializationResult materialization;
        await using (var db = new FactoryDbContext(_options))
        {
            materialization = await CreatePlanner(db).MaterializeAsync(runId);
        }

        Assert.Equal(PlannerMaterializationOutcome.PlanValidationFailed, materialization.Outcome);
        Assert.Null(materialization.Plan);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Empty(await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId));
    }

    [Fact]
    public async Task Materialize_NonJsonAgentOutput_IsCandidateInvalid_NoPartialWorkItems()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, "not json at all")).ExecuteAsync(runId);
        }

        PlannerMaterializationResult materialization;
        await using (var db = new FactoryDbContext(_options))
        {
            materialization = await CreatePlanner(db).MaterializeAsync(runId);
        }

        Assert.Equal(PlannerMaterializationOutcome.CandidateInvalid, materialization.Outcome);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Empty(await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId));
    }

    [Theory]
    [InlineData(FakeAgentRuntime.Mode.Failure)]
    [InlineData(FakeAgentRuntime.Mode.Timeout)]
    public async Task Materialize_RuntimeFailure_IsRuntimeFailed_NoWorkItemsCreated(FakeAgentRuntime.Mode mode)
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(mode)).ExecuteAsync(runId);
        }

        PlannerMaterializationResult materialization;
        await using (var db = new FactoryDbContext(_options))
        {
            materialization = await CreatePlanner(db).MaterializeAsync(runId);
        }

        Assert.Equal(PlannerMaterializationOutcome.RuntimeFailed, materialization.Outcome);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Empty(await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId));
    }

    [Fact]
    public async Task Materialize_CancelledAgentRun_IsRuntimeFailed_NoWorkItemsCreated()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
            var repo = new AgentRunRepository(db);
            var run = (await repo.GetByIdAsync(runId))!;
            run.TransitionTo(AgentRunStatus.Running);
            run.TransitionTo(AgentRunStatus.Cancelled);
            await repo.UpdateAsync(run);
        }

        PlannerMaterializationResult materialization;
        await using (var db = new FactoryDbContext(_options))
        {
            materialization = await CreatePlanner(db).MaterializeAsync(runId);
        }

        Assert.Equal(PlannerMaterializationOutcome.RuntimeFailed, materialization.Outcome);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Empty(await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId));
    }

    // ---------------- Idempotency/concurrency (§27 items 21-23) ----------------

    [Fact]
    public async Task GenerateAsync_CalledAgain_WhileFirstRunStillActive_ReusesSameAgentRun()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);

        await using var db = new FactoryDbContext(_options);
        var planner = CreatePlanner(db);

        var first = await planner.GenerateAsync(projectId);
        var second = await planner.GenerateAsync(projectId);

        Assert.Equal(first.Id, second.Id);

        var page = await new AgentRunRepository(db).GetPagedAsync(new AgentRunQuery(ProjectId: projectId));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task GenerateAsync_ProjectAlreadyHasPersistedPlan_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync(ProjectPlanner.PlannerAgentType);
        await SeedAgentDefinitionAsync("Implementation");
        await SeedAgentDefinitionAsync("Test");

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            runId = (await CreatePlanner(db).GenerateAsync(projectId)).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateOrchestrator(db, new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, ValidCandidateJson)).ExecuteAsync(runId);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var materialization = await CreatePlanner(db).MaterializeAsync(runId);
            Assert.Equal(PlannerMaterializationOutcome.Succeeded, materialization.Outcome);
        }

        await using var finalDb = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreatePlanner(finalDb).GenerateAsync(projectId));
    }
}
