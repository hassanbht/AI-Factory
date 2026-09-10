using AI.Factory.Application.Abstractions;
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

/// <summary>Phase 18.3 §17 — deterministic selection, dependency, dispatch, idempotency, concurrency, race, and failure-isolation coverage for TaskDispatchScheduler.</summary>
public sealed class TaskDispatchSchedulerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public TaskDispatchSchedulerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static TestOperationalMetrics NewMetrics() => new();

    private static TaskDispatchScheduler CreateScheduler(
        FactoryDbContext db, IOperationalMetrics? metrics = null, TaskSchedulerOptions? options = null) =>
        new(
            new WorkItemRepository(db),
            new TaskDependencyResolver(),
            new AgentRunRepository(db),
            new AgentDefinitionRepository(db),
            options ?? new TaskSchedulerOptions { MaxConcurrentTasks = 1 },
            metrics ?? NewMetrics(),
            NullLogger<TaskDispatchScheduler>.Instance);

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Scheduler Project", "d");
        return project.Id;
    }

    private async Task<Guid> SeedAgentDefinitionAsync(string agentType = "Implementation")
    {
        await using var db = new FactoryDbContext(_options);
        var created = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}",
            AgentType = agentType,
            SystemPrompt = "You do the work."
        });
        return created.Id;
    }

    private async Task<Guid> SeedReadyTaskAsync(
        Guid projectId, string title, string priority = "Normal", DateTime? createdAtUtc = null)
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, title, null, priority);

        if (createdAtUtc is { } created)
        {
            item.CreatedAtUtc = created;
            await new WorkItemRepository(db).UpdateAsync(item);
        }

        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        return item.Id;
    }

    private async Task<Guid> SeedReadyTaskWithoutAgentAsync(Guid projectId)
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Review, "No agent for this type", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        return item.Id;
    }

    // ---------------- Selection ----------------

    [Fact]
    public async Task RunCycleAsync_OneReadyTask_Dispatches()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var taskId = await SeedReadyTaskAsync(projectId, "A");

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        Assert.Equal(1, result.DispatchedCount);
        Assert.Equal(taskId, result.Outcomes.Single().WorkItemId);
        Assert.NotNull(result.Outcomes.Single().AgentRunId);
    }

    [Fact]
    public async Task RunCycleAsync_HigherPriorityDispatchedFirst()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var low = await SeedReadyTaskAsync(projectId, "Low", priority: "Low", createdAtUtc: DateTime.UtcNow.AddMinutes(-5));
        var high = await SeedReadyTaskAsync(projectId, "High", priority: "High", createdAtUtc: DateTime.UtcNow);

        await using var db = new FactoryDbContext(_options);
        var options = new TaskSchedulerOptions { MaxConcurrentTasks = 1 };
        var result = await CreateScheduler(db, options: options).RunCycleAsync(projectId);

        Assert.Equal(high, result.Outcomes.Single(x => x.Dispatched).WorkItemId);
        Assert.DoesNotContain(result.Outcomes, x => x.WorkItemId == low && x.Dispatched);
    }

    [Fact]
    public async Task RunCycleAsync_StableIdTieBreaker_SamePriorityAndCreatedAt()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var sameTime = DateTime.UtcNow;
        var a = await SeedReadyTaskAsync(projectId, "A", createdAtUtc: sameTime);
        var b = await SeedReadyTaskAsync(projectId, "B", createdAtUtc: sameTime);
        var expectedFirst = a.CompareTo(b) <= 0 ? a : b;

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db).RunCycleAsync(projectId);

        Assert.Equal(expectedFirst, result.Outcomes.Single(x => x.Dispatched).WorkItemId);
    }

    // ---------------- Dependency behavior ----------------

    [Fact]
    public async Task RunCycleAsync_PendingDependency_NotDispatched()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var blocker = await SeedReadyTaskAsync(projectId, "Blocker");
        var dependent = await SeedReadyTaskAsync(projectId, "Dependent");

        await using (var db = new FactoryDbContext(_options))
        {
            await new TaskDependencyService(new WorkItemRepository(db), new TaskDependencyResolver())
                .AddDependencyAsync(dependent, blocker);
        }

        await using var runDb = new FactoryDbContext(_options);
        var result = await CreateScheduler(runDb, options: new TaskSchedulerOptions { MaxConcurrentTasks = 2 })
            .RunCycleAsync(projectId);

        Assert.Single(result.Outcomes);
        Assert.Equal(blocker, result.Outcomes.Single().WorkItemId);
    }

    // ---------------- Dispatch ----------------

    [Fact]
    public async Task RunCycleAsync_Dispatch_CreatesQueuedAgentRunLinkedToProjectAndWorkItem_AndMarksWorkItemRunning()
    {
        var projectId = await SeedProjectAsync();
        var agentDefinitionId = await SeedAgentDefinitionAsync();
        var taskId = await SeedReadyTaskAsync(projectId, "A");

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateScheduler(db).RunCycleAsync(projectId);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var run = await verifyDb.AgentRuns.FirstOrDefaultAsync(x => x.WorkItemId == taskId);

        Assert.NotNull(run);
        Assert.Equal(AgentRunStatus.Queued, run!.Status);
        Assert.Equal(projectId, run.ProjectId);
        Assert.Equal(taskId, run.WorkItemId);
        Assert.Equal(agentDefinitionId, run.AgentDefinitionId);

        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem!.Status);
    }

    // ---------------- Idempotency ----------------

    [Fact]
    public async Task RunCycleAsync_CalledTwice_OnlyOneActiveAgentRun()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var taskId = await SeedReadyTaskAsync(projectId, "A");

        await using (var db1 = new FactoryDbContext(_options))
        {
            await CreateScheduler(db1).RunCycleAsync(projectId);
        }

        await using (var db2 = new FactoryDbContext(_options))
        {
            var second = await CreateScheduler(db2).RunCycleAsync(projectId);
            Assert.Equal(0, second.DispatchedCount);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var runCount = await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == taskId);
        Assert.Equal(1, runCount);
    }

    // ---------------- Concurrency ----------------

    [Fact]
    public async Task RunCycleAsync_MoreReadyThanConcurrencyLimit_OnlyLimitDispatched()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        await SeedReadyTaskAsync(projectId, "A", createdAtUtc: DateTime.UtcNow.AddMinutes(-3));
        await SeedReadyTaskAsync(projectId, "B", createdAtUtc: DateTime.UtcNow.AddMinutes(-2));
        await SeedReadyTaskAsync(projectId, "C", createdAtUtc: DateTime.UtcNow.AddMinutes(-1));

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db, options: new TaskSchedulerOptions { MaxConcurrentTasks = 2 })
            .RunCycleAsync(projectId);

        Assert.Equal(2, result.DispatchedCount);
        Assert.Equal(1, result.SkippedDueToConcurrencyLimit);

        var remainingReady = (await new WorkItemRepository(db).GetByProjectIdAsync(projectId))
            .Count(x => x.Status == WorkItemStatus.Ready.ToString());
        Assert.Equal(1, remainingReady);
    }

    // ---------------- Race condition ----------------

    [Fact]
    public async Task RunCycleAsync_ConcurrentCycles_NoDuplicateDispatch()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync();
        var taskId = await SeedReadyTaskAsync(projectId, "A");

        await using var dbA = new FactoryDbContext(_options);
        await using var dbB = new FactoryDbContext(_options);

        var schedulerA = CreateScheduler(dbA, options: new TaskSchedulerOptions { MaxConcurrentTasks = 1 });
        var schedulerB = CreateScheduler(dbB, options: new TaskSchedulerOptions { MaxConcurrentTasks = 1 });

        var taskA = schedulerA.RunCycleAsync(projectId);
        var taskB = schedulerB.RunCycleAsync(projectId);
        var resultsAll = await Task.WhenAll(taskA, taskB);

        var totalDispatched = resultsAll.Sum(x => x.DispatchedCount);
        Assert.Equal(1, totalDispatched);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(1, await verifyDb.AgentRuns.CountAsync(x => x.WorkItemId == taskId));

        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem!.Status);
    }

    // ---------------- Failure isolation ----------------

    [Fact]
    public async Task RunCycleAsync_NoMatchingAgentDefinition_SkipsWithoutClaimingWorkItem_AndDoesNotBlockOthers()
    {
        var projectId = await SeedProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");
        var noDefinitionTask = await SeedReadyTaskWithoutAgentAsync(projectId);
        var dispatchable = await SeedReadyTaskAsync(projectId, "Dispatchable");

        await using var db = new FactoryDbContext(_options);
        var result = await CreateScheduler(db, options: new TaskSchedulerOptions { MaxConcurrentTasks = 2 })
            .RunCycleAsync(projectId);

        Assert.Equal(1, result.DispatchedCount);
        Assert.Contains(result.Outcomes, x => x.WorkItemId == dispatchable && x.Dispatched);
        Assert.Contains(result.Outcomes, x =>
            x.WorkItemId == noDefinitionTask && !x.Dispatched
            && x.SkipReason == TaskDispatchSkipReason.NoAgentDefinition);

        var untouched = await new WorkItemRepository(db).GetByIdAsync(noDefinitionTask);
        Assert.Equal(WorkItemStatus.Ready.ToString(), untouched!.Status);
    }

    private sealed class TestOperationalMetrics : IOperationalMetrics
    {
        public void RecordOperationStarted() { }
        public void RecordOperationCompleted(bool success) { }
        public void RecordAgentExecution(bool success, long? durationMs) { }
        public void RecordBuildExecution(bool success, long? durationMs) { }
        public void RecordTestExecution(bool success, long? durationMs) { }
        public void RecordRepairAttempt(bool success, long? durationMs) { }
        public void RecordApproval(bool approved) { }
        public void RecordRetryAttempt() { }
        public void RecordRetrySuccess() { }
        public void RecordRetryExhausted() { }
        public void RecordRetryCancelled() { }
        public void RecordAttemptTimeout() { }
        public void RecordOperationTimeout() { }
        public void RecordCircuitOpened() { }
        public void RecordCircuitClosed() { }
        public void RecordCircuitHalfOpen() { }
        public void RecordCircuitRejected() { }
        public void RecordBulkheadEntered() { }
        public void RecordBulkheadQueued() { }
        public void RecordBulkheadRejected() { }
        public void RecordBulkheadCompleted() { }
        public void RecordShutdownInitiated() { }
        public void RecordWorkerStopping() { }
        public void RecordWorkerStopped() { }
        public void RecordWorkerPollFailure() { }
        public void RecordWorkerDegraded() { }
        public void RecordWorkerRecovered() { }
        public void RecordWorkerFaulted() { }
        public void RecordOperationsDrained(int count) { }
        public void RecordOperationsCancelled(int count) { }
        public void RecordShutdownTimedOut() { }
        public void RecordShutdownRequestRejected() { }
        public void RecordRateLimitRejected() { }
        public void RecordSecurityRequestRejected() { }
        public void RecordOperationalAccessDenied() { }
        public void RecordConfigurationValidationFailed() { }
        public void RecordSecretResolutionFailed() { }
        public void RecordRequiredSecretMissing() { }
        public void RecordDatabaseOperation(bool success, long? durationMs = null) { }
        public void RecordDatabaseRetry() { }
        public void RecordDatabaseTransientFailure() { }
        public void RecordDatabaseConcurrencyConflict() { }
        public void RecordDatabaseBusyFailure() { }
        public void RecordDatabaseLockedFailure() { }
        public void RecordDatabaseTransactionFailure() { }
        public void RecordBackupSucceeded() { }
        public void RecordBackupFailed() { }
        public void RecordBackupValidated() { }
        public void RecordRestoreSucceeded() { }
        public void RecordRestoreFailed() { }
        public void RecordAiRequestStarted() { }
        public void RecordAiRequestSucceeded(long? durationMs) { }
        public void RecordAiRequestFailed() { }
        public void RecordAiProviderFailure() { }
        public void RecordAiRateLimited() { }
        public void RecordAiRoutingFallback() { }
        public void RecordAgentRunCreated() { }
        public void RecordAgentRunStarted() { }
        public void RecordAgentRunSucceeded() { }
        public void RecordAgentRunFailed() { }
        public void RecordAgentRunTimedOut() { }
        public void RecordAgentRunCancelled() { }
        public void RecordAgentWorkerClaimSuccess() { }
        public void RecordSchedulingCycleRun() { }
        public void RecordSchedulerTaskDispatched() { }
        public void RecordSchedulerDispatchFailure() { }
        public void RecordSchedulerRaceLost() { }
        public void RecordSchedulerConcurrencyLimitSkipped(int count) { }

        public OperationalMetricsSnapshot GetSnapshot() => new(
            DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null);
    }
}
