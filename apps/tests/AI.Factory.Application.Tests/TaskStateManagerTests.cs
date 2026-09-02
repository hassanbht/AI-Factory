using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class TaskStateManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public TaskStateManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Task DAG Test",
            Slug = "task-dag-test",
            RootPath = "projects/task-dag-test",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private async Task<Guid> SeedWorkItemAsync(int maxAttempts = 3)
    {
        var id = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);

        db.WorkItems.Add(new WorkItem
        {
            Id = id,
            ProjectId = _projectId,
            Title = "seeded task",
            Status = WorkItemStatus.Draft.ToString(),
            MaxAttempts = maxAttempts,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        return id;
    }

    [Fact]
    public async Task TryStartAsync_PersistsRunningStatus_AcrossNewDbContextInstance()
    {
        var workItemId = await SeedWorkItemAsync();

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            var started = await manager.TryStartAsync(workItemId);
            Assert.True(started);
        }

        // New DbContext instance stands in for a process restart.
        await using (var readDb = new FactoryDbContext(_options))
        {
            var item = await readDb.WorkItems.SingleAsync(x => x.Id == workItemId);

            Assert.Equal(WorkItemStatus.Running.ToString(), item.Status);
            Assert.Equal(1, item.AttemptCount);
            Assert.NotNull(item.StartedAtUtc);
        }
    }

    [Fact]
    public async Task TryStartAsync_ReturnsFalse_WhenAlreadyRunning()
    {
        var workItemId = await SeedWorkItemAsync();

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        var first = await manager.TryStartAsync(workItemId);
        var second = await manager.TryStartAsync(workItemId);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task CompleteAsync_SetsCompletedStatus()
    {
        var workItemId = await SeedWorkItemAsync();

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        await manager.TryStartAsync(workItemId);
        await manager.CompleteAsync(workItemId);

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), item.Status);
        Assert.NotNull(item.CompletedAtUtc);
    }

    [Fact]
    public async Task FailAsync_TransitionsToRepairing_WhenAttemptsRemain()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        await manager.TryStartAsync(workItemId);
        await manager.FailAsync(workItemId, "build failed");

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), item.Status);
        Assert.Equal("build failed", item.CurrentError);
        Assert.Equal(1, item.AttemptCount);
    }

    [Fact]
    public async Task FailAsync_SetsFailed_WhenAttemptsExhausted()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        await manager.TryStartAsync(workItemId);
        await manager.FailAsync(workItemId, "build failed again");

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), item.Status);
    }

    [Fact]
    public async Task FailAsync_SetsFailed_WhenGlobalMaxRepairAttempts_IsSmallerThanWorkItemMaxAttempts()
    {
        // WorkItem itself allows 10 attempts, but the global ceiling is 1 —
        // the smaller of the two must win.
        var workItemId = await SeedWorkItemAsync(maxAttempts: 10);

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db, new ExecutionLimitsOptions { MaxRepairAttempts = 1 });

        await manager.TryStartAsync(workItemId);
        await manager.FailAsync(workItemId, "build failed");

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), item.Status);
    }

    [Fact]
    public async Task RecoverOrphanedTasksAsync_TransitionsRunningTask_ToRepairing_WhenAttemptsRemain()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            await manager.TryStartAsync(workItemId);
        }

        // A brand-new DbContext + manager instance stands in for a process restart.
        await using var recoveryDb = new FactoryDbContext(_options);
        var recoveryManager = new TaskStateManager(recoveryDb);

        await recoveryManager.RecoverOrphanedTasksAsync();

        var item = await recoveryDb.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), item.Status);
        Assert.Contains("restart", item.CurrentError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverOrphanedTasksAsync_TransitionsRunningTask_ToFailed_WhenAttemptsExhausted()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            await manager.TryStartAsync(workItemId);
        }

        await using var recoveryDb = new FactoryDbContext(_options);
        var recoveryManager = new TaskStateManager(recoveryDb);

        await recoveryManager.RecoverOrphanedTasksAsync();

        var item = await recoveryDb.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), item.Status);
    }

    [Fact]
    public async Task HeartbeatAsync_UpdatesLastHeartbeat_AndCheckpoint()
    {
        var workItemId = await SeedWorkItemAsync();

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        await manager.TryStartAsync(workItemId);
        await manager.HeartbeatAsync(workItemId, "AgentExecutionCompleted");

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.NotNull(item.LastHeartbeatAtUtc);
        Assert.Equal("AgentExecutionCompleted", item.LastCheckpoint);
    }

    [Fact]
    public async Task RecoverStaleExecutionsAsync_RecoversRunningItem_WithStaleHeartbeat()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            await manager.TryStartAsync(workItemId);

            // Simulate a heartbeat from long ago — the process behind it is presumed dead.
            var item = await writeDb.WorkItems.SingleAsync(x => x.Id == workItemId);
            item.LastHeartbeatAtUtc = DateTime.UtcNow.AddHours(-1);
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var recoveryManager = new TaskStateManager(
            db, recoveryOptions: new RecoveryOptions { StaleExecutionTimeout = TimeSpan.FromMinutes(15) });

        var recoveredCount = await recoveryManager.RecoverStaleExecutionsAsync();

        Assert.Equal(1, recoveredCount);

        var recovered = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), recovered.Status);
        Assert.Contains("stale-execution", recovered.CurrentError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverStaleExecutionsAsync_LeavesFreshlyHeartbeatingItem_Untouched()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(
            db, recoveryOptions: new RecoveryOptions { StaleExecutionTimeout = TimeSpan.FromMinutes(15) });

        await manager.TryStartAsync(workItemId);
        await manager.HeartbeatAsync(workItemId, "AgentExecutionCompleted");

        var recoveredCount = await manager.RecoverStaleExecutionsAsync();

        Assert.Equal(0, recoveredCount);

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Running.ToString(), item.Status);
    }

    [Fact]
    public async Task RecoverStaleExecutionsAsync_PreservesLastCheckpoint_ForInterruptedExecution()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            await manager.TryStartAsync(workItemId);
            await manager.HeartbeatAsync(workItemId, "AgentExecutionCompleted");

            var item = await writeDb.WorkItems.SingleAsync(x => x.Id == workItemId);
            item.LastHeartbeatAtUtc = DateTime.UtcNow.AddHours(-1);
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var recoveryManager = new TaskStateManager(
            db, recoveryOptions: new RecoveryOptions { StaleExecutionTimeout = TimeSpan.FromMinutes(15) });

        await recoveryManager.RecoverStaleExecutionsAsync();

        var recovered = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), recovered.Status);
        Assert.Equal("AgentExecutionCompleted", recovered.LastCheckpoint);
    }

    [Fact]
    public async Task RecoverStaleExecutionsAsync_IsIdempotent_WhenCalledRepeatedly()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var manager = new TaskStateManager(writeDb);
            await manager.TryStartAsync(workItemId);

            var item = await writeDb.WorkItems.SingleAsync(x => x.Id == workItemId);
            item.LastHeartbeatAtUtc = DateTime.UtcNow.AddHours(-1);
            await writeDb.SaveChangesAsync();
        }

        var options = new RecoveryOptions { StaleExecutionTimeout = TimeSpan.FromMinutes(15) };

        await using var db1 = new FactoryDbContext(_options);
        var first = await new TaskStateManager(db1, recoveryOptions: options).RecoverStaleExecutionsAsync();

        await using var db2 = new FactoryDbContext(_options);
        var second = await new TaskStateManager(db2, recoveryOptions: options).RecoverStaleExecutionsAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task RecoverOrphanedTasksAsync_LeavesNonRunningTasks_Untouched()
    {
        var workItemId = await SeedWorkItemAsync();

        await using var db = new FactoryDbContext(_options);
        var manager = new TaskStateManager(db);

        await manager.RecoverOrphanedTasksAsync();

        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Draft.ToString(), item.Status);
    }
}
