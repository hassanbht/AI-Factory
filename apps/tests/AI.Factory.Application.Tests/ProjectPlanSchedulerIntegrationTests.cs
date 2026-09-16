using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
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
/// Phase 18.5 §18/§23 — the actual proof that a Planner-persisted plan is
/// consumable by the existing Phase 18.3 scheduler: Plan -> WorkItems ->
/// Dependencies -> Readiness -> TaskDispatchScheduler -> AgentRun, without
/// re-implementing readiness, dependency DFS, ordering, or AgentRun
/// claiming — all reused as-is from their existing owners.
/// </summary>
public sealed class ProjectPlanSchedulerIntegrationTests : IDisposable
{
    /// <summary>Phase 18.22 §2 — a new plan's tasks must declare machine-checkable evidence of completion.</summary>
    private const string ValidCriteria =
        """{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}""";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectPlanSchedulerIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static ProjectPlanService CreatePlanService(FactoryDbContext db) => new(
        new ProjectRepository(db),
        new WorkItemRepository(db),
        new TaskDependencyResolver(),
        new AgentDefinitionRepository(db),
        new DatabaseTransactionManager(db, new OperationalMetrics(), NullLogger<DatabaseTransactionManager>.Instance),
        NullLogger<ProjectPlanService>.Instance);

    /// <summary>This file never drives a WorkItem/Project to completion, so the Phase 18.10 completion gate's verification call is never expected to run.</summary>
    private sealed class NotCalledVerificationGate : IVerificationGate
    {
        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyAsync should not be called in this test file.");

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyProjectAsync should not be called in this test file.");
    }

    private static TaskDispatchScheduler CreateScheduler(FactoryDbContext db) => new(
        new WorkItemRepository(db),
        new ProjectRepository(db),
        new TaskDependencyResolver(),
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        new TaskSchedulerOptions { MaxConcurrentTasks = 2 },
        new ExecutionLimitsOptions(),
        new ProjectCompletionGate(
            new ProjectRepository(db), new AgentRunRepository(db), new NotCalledVerificationGate(),
            NullLogger<ProjectCompletionGate>.Instance),
        new OperationalMetrics(),
        NullLogger<TaskDispatchScheduler>.Instance);

    [Fact]
    public async Task PlannedDag_IsConsumedByScheduler_RootDispatched_DependentBlocked()
    {
        Guid projectId;
        await using (var db = new FactoryDbContext(_options))
        {
            var projectService = new ProjectService(new ProjectRepository(db));
            var project = await projectService.CreateAsync("Integration Project", "d");
            projectId = project.Id;
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Analyzing);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Specification);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Architecture);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Planning);

            await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
            {
                Name = "Implementer",
                AgentType = "Implementation",
                SystemPrompt = "x"
            });
        }

        Guid rootWorkItemId, dependentWorkItemId;
        await using (var db = new FactoryDbContext(_options))
        {
            var plan = await CreatePlanService(db).CreatePlanAsync(new ProjectPlanRequest(
                projectId, "Integration plan",
                new[]
                {
                    new PlannedWorkItemInput("root", "Root task", "d", WorkItemType.Implementation, "Normal", Array.Empty<string>(), ValidCriteria),
                    new PlannedWorkItemInput("dependent", "Dependent task", "d", WorkItemType.Implementation, "Normal", new[] { "root" }, ValidCriteria)
                }));

            rootWorkItemId = plan.Tasks.Single(t => t.PlanKey == "root").WorkItemId;
            dependentWorkItemId = plan.Tasks.Single(t => t.PlanKey == "dependent").WorkItemId;
        }

        // First scheduler cycle: only the dependency-free root is eligible.
        await using (var db = new FactoryDbContext(_options))
        {
            var cycleResult = await CreateScheduler(db).RunCycleAsync(projectId);

            Assert.Equal(1, cycleResult.DispatchedCount);
            Assert.Equal(rootWorkItemId, cycleResult.Outcomes.Single(o => o.Dispatched).WorkItemId);
        }

        await using (var verifyDb = new FactoryDbContext(_options))
        {
            var root = await new WorkItemRepository(verifyDb).GetByIdAsync(rootWorkItemId);
            var dependent = await new WorkItemRepository(verifyDb).GetByIdAsync(dependentWorkItemId);

            Assert.Equal(WorkItemStatus.Running.ToString(), root!.Status);
            Assert.Equal(WorkItemStatus.Ready.ToString(), dependent!.Status);
            Assert.Equal(1, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == rootWorkItemId));
            Assert.Equal(0, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == dependentWorkItemId));
        }

        // Complete the root the same way AgentRunWorkItemSync would, then
        // the dependent becomes eligible on the next cycle.
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemRepository(db);
            var root = await workItems.GetByIdAsync(rootWorkItemId);
            root!.TransitionTo(WorkItemStatus.Completed);
            await workItems.UpdateAsync(root);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var cycleResult = await CreateScheduler(db).RunCycleAsync(projectId);

            Assert.Equal(1, cycleResult.DispatchedCount);
            Assert.Equal(dependentWorkItemId, cycleResult.Outcomes.Single(o => o.Dispatched).WorkItemId);
        }

        await using var finalDb = new FactoryDbContext(_options);
        Assert.Equal(1, await finalDb.AgentRuns.CountAsync(x => x.WorkItemId == dependentWorkItemId));
    }
}
