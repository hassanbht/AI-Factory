using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
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
/// Phase 18.9 §19 "Tests — Mandatory" — scheduler-level repair dispatch
/// coverage (dispatch, idempotency/concurrency, persistence, safety).
/// Verification-loop and repair-failure-policy coverage that needs a real
/// AgentOrchestrator lives in RepairVerificationLoopTests.cs instead.
/// </summary>
public sealed class RepairDispatchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public RepairDispatchTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>This file never drives a WorkItem/Project to completion, so the Phase 18.10 completion gate's verification call is never expected to run.</summary>
    private sealed class NotCalledVerificationGate : IVerificationGate
    {
        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyAsync should not be called in this test file.");

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VerifyProjectAsync should not be called in this test file.");
    }

    private static TaskDispatchScheduler CreateScheduler(
        FactoryDbContext db, TaskSchedulerOptions? options = null, ExecutionLimitsOptions? executionLimits = null) =>
        new(
            new WorkItemRepository(db),
            new ProjectRepository(db),
            new TaskDependencyResolver(),
            new AgentRunRepository(db),
            new AgentDefinitionRepository(db),
            options ?? new TaskSchedulerOptions { MaxConcurrentTasks = 2 },
            executionLimits ?? new ExecutionLimitsOptions(),
            new ProjectCompletionGate(
                new ProjectRepository(db), new AgentRunRepository(db), new NotCalledVerificationGate(),
                NullLogger<ProjectCompletionGate>.Instance),
            new OperationalMetrics(),
            NullLogger<TaskDispatchScheduler>.Instance);

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Repair Project", "d");
        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file is not exercising source init.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}", AgentType = agentType, SystemPrompt = "x"
        });
    }

    /// <summary>
    /// Drives a fresh WorkItem through the only real path into Repairing
    /// (Ready -> Running -> Testing -> Repairing, all valid WorkItemTransitions),
    /// then sets CurrentError/RepairAttemptCount directly — exactly the state
    /// AgentRunWorkItemSync.FinalizeVerificationAsync leaves behind after a
    /// VerificationStatus.Failed result (Phase 18.8).
    /// </summary>
    private async Task<Guid> SeedRepairingWorkItemAsync(
        Guid projectId, string currentError = "Build failed: error CS1002", int repairAttemptCount = 0)
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Testing);
        var workItem = await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Repairing);

        workItem.CurrentError = currentError;
        workItem.RepairAttemptCount = repairAttemptCount;
        await new WorkItemRepository(db).UpdateAsync(workItem);

        return item.Id;
    }

    // ---------------- Repair dispatch ----------------

    [Fact]
    public async Task RepairingWorkItem_CreatesRepairAgentRun()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        Assert.Equal(1, result.DispatchedCount);
        var outcome = result.Outcomes.Single();
        Assert.Equal(workItemId, outcome.WorkItemId);
        Assert.NotNull(outcome.AgentRunId);
    }

    [Fact]
    public async Task RepairingWorkItem_UsesRepairAgentDefinition()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        var run = await new AgentRunRepository(db).GetByIdAsync(result.Outcomes.Single().AgentRunId!.Value);
        var repairDefinition = (await new AgentDefinitionRepository(db).GetAllAsync())
            .Single(x => x.AgentType == TaskDispatchScheduler.RepairAgentType);

        Assert.Equal(repairDefinition.Id, run!.AgentDefinitionId);
    }

    [Fact]
    public async Task RepairingWorkItem_LinksProjectAndWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        var run = await new AgentRunRepository(db).GetByIdAsync(result.Outcomes.Single().AgentRunId!.Value);

        Assert.Equal(projectId, run!.ProjectId);
        Assert.Equal(workItemId, run.WorkItemId);
    }

    [Fact]
    public async Task RepairingWorkItem_TransitionsToRunning()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db = new FactoryDbContext(_options);
        await CreateScheduler(db).RunCycleAsync(projectId);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem!.Status);
    }

    // ---------------- Idempotency / concurrency ----------------

    [Fact]
    public async Task ConcurrentRepairDispatch_CreatesOnlyOneRepairAgentRun()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var dbA = new FactoryDbContext(_options);
        await using var dbB = new FactoryDbContext(_options);

        var schedulerA = CreateScheduler(dbA);
        var schedulerB = CreateScheduler(dbB);

        var resultsAll = await Task.WhenAll(
            schedulerA.RunCycleAsync(projectId), schedulerB.RunCycleAsync(projectId));

        Assert.Equal(1, resultsAll.Sum(x => x.DispatchedCount));

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(1, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
    }

    [Fact]
    public async Task DuplicateRepairObservation_IsSafe()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using (var db1 = new FactoryDbContext(_options))
        {
            var first = await CreateScheduler(db1).RunCycleAsync(projectId);
            Assert.Equal(1, first.DispatchedCount);
        }

        await using (var db2 = new FactoryDbContext(_options))
        {
            // The WorkItem is now Running, not Repairing — a second cycle
            // must not re-dispatch it as a repair candidate.
            var second = await CreateScheduler(db2).RunCycleAsync(projectId);
            Assert.Equal(0, second.DispatchedCount);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(1, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
    }

    [Fact]
    public async Task RepairClaim_UsesOptimisticConcurrency()
    {
        // Simulates a genuine lost race: db1 loads the WorkItem, db2 claims
        // and persists first, then db1's own (stale-Version) claim attempt
        // must be rejected rather than silently overwriting db2's write.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db1 = new FactoryDbContext(_options);
        var staleWorkItem = await new WorkItemRepository(db1).GetByIdAsync(workItemId);

        await using (var db2 = new FactoryDbContext(_options))
        {
            var winner = await CreateScheduler(db2).RunCycleAsync(projectId);
            Assert.Equal(1, winner.DispatchedCount);
        }

        staleWorkItem!.RepairAttemptCount += 1;
        staleWorkItem.TransitionTo(WorkItemStatus.Running);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new WorkItemRepository(db1).UpdateAsync(staleWorkItem));

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(1, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
    }

    // ---------------- Repair failure — budget exhaustion ----------------

    [Fact]
    public async Task MaxRepairAttempts_FailsWorkItem()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var limits = new ExecutionLimitsOptions { MaxRepairAttempts = 2 };
        var workItemId = await SeedRepairingWorkItemAsync(
            projectId, currentError: "Test failed: 1 assertion", repairAttemptCount: 2);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db, executionLimits: limits).RunCycleAsync(projectId);

        Assert.Equal(0, result.DispatchedCount);
        var outcome = result.Outcomes.Single();
        Assert.Equal(TaskDispatchSkipReason.RepairBudgetExhausted, outcome.SkipReason);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
        Assert.Contains("Maximum repair attempts", workItem.CurrentError);
        Assert.Contains("Test failed: 1 assertion", workItem.CurrentError);
    }

    // ---------------- Persistence ----------------

    [Fact]
    public async Task RepairAttemptCount_IsPersisted()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db).RunCycleAsync(projectId);
        }

        await using var freshDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(freshDb).GetByIdAsync(workItemId);
        Assert.Equal(1, workItem!.RepairAttemptCount);
    }

    [Fact]
    public async Task RepairClaim_IsAtomic()
    {
        // The attempt-count increment and the Repairing -> Running
        // transition are written in a single UpdateAsync call — a lost race
        // must leave BOTH unchanged together, never just one of them.
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db1 = new FactoryDbContext(_options);
        var dbA = new FactoryDbContext(_options);
        var dbB = new FactoryDbContext(_options);

        var schedulerA = CreateScheduler(dbA);
        var schedulerB = CreateScheduler(dbB);
        await Task.WhenAll(schedulerA.RunCycleAsync(projectId), schedulerB.RunCycleAsync(projectId));
        await dbA.DisposeAsync();
        await dbB.DisposeAsync();

        var workItem = await new WorkItemRepository(db1).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem!.Status);
        Assert.Equal(1, workItem.RepairAttemptCount);
    }

    // ---------------- Safety ----------------

    [Fact]
    public async Task MissingRepairAgentDefinition_FailsClosed()
    {
        var projectId = await SeedProjectAsync();
        // Only an Implementation AgentDefinition exists — no Repair one.
        await SeedAgentDefinitionAsync("Implementation");
        var workItemId = await SeedRepairingWorkItemAsync(projectId);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        Assert.Equal(0, result.DispatchedCount);
        var outcome = result.Outcomes.Single();
        Assert.Equal(TaskDispatchSkipReason.NoRepairAgentDefinition, outcome.SkipReason);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem!.Status);
        Assert.Equal(0, workItem.RepairAttemptCount);
        Assert.Equal(0, await db.AgentRuns.CountAsync(x => x.WorkItemId == workItemId));
    }

    [Fact]
    public async Task RepairInput_IsBounded()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync(TaskDispatchScheduler.RepairAgentType);
        var hugeFailure = string.Join("\n", Enumerable.Repeat("error CS0000: something went wrong", 500));
        var workItemId = await SeedRepairingWorkItemAsync(projectId, currentError: hugeFailure);

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        var run = await new AgentRunRepository(db).GetByIdAsync(result.Outcomes.Single().AgentRunId!.Value);

        Assert.True(run!.Input!.Length < hugeFailure.Length);
    }
}
