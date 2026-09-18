using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.4 §8/§22 — proves the actual claim: a genuinely heartbeating
/// AgentRun-backed WorkItem is never falsely recovered as stale, a truly
/// stale one still is, and the interaction between heartbeat and
/// WorkflowRecoveryHostedService's existing recovery is safe under the
/// existing optimistic-concurrency mechanism (no new locking).
/// </summary>
public sealed class AgentRunHeartbeatRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunHeartbeatRecoveryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedRunningTaskAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Heartbeat Project", "d");
        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file is not exercising source init.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        return item.Id;
    }

    private static TaskStateManager CreateTaskStateManager(FactoryDbContext db, TimeSpan staleTimeout) =>
        new(db, recoveryOptions: new RecoveryOptions { StaleExecutionTimeout = staleTimeout });

    [Fact]
    public async Task FreshHeartbeat_PreventsStaleRecovery()
    {
        var workItemId = await SeedRunningTaskAsync();
        var staleTimeout = TimeSpan.FromMilliseconds(80);

        // Simulate AgentRunWorker's heartbeat loop keeping the WorkItem
        // fresh across a window longer than the stale timeout.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(30);
            await using var heartbeatDb = new FactoryDbContext(_options);
            await CreateTaskStateManager(heartbeatDb, staleTimeout).HeartbeatAsync(workItemId, "InProgress");
        }

        await using var recoveryDb = new FactoryDbContext(_options);
        var recoveredCount = await CreateTaskStateManager(recoveryDb, staleTimeout).RecoverStaleExecutionsAsync();

        Assert.Equal(0, recoveredCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task StaleHeartbeat_BecomesRecoverable()
    {
        var workItemId = await SeedRunningTaskAsync();
        var staleTimeout = TimeSpan.FromMilliseconds(60);

        // No heartbeat calls at all — just wait past the stale timeout.
        await Task.Delay(120);

        await using var recoveryDb = new FactoryDbContext(_options);
        var recoveredCount = await CreateTaskStateManager(recoveryDb, staleTimeout).RecoverStaleExecutionsAsync();

        Assert.Equal(1, recoveredCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.NotEqual(WorkItemStatus.Running.ToString(), workItem!.Status);
        Assert.True(
            workItem.Status is nameof(WorkItemStatus.Repairing) or nameof(WorkItemStatus.Failed),
            $"Expected Repairing or Failed, got {workItem.Status}.");
        Assert.Contains("stale-execution timeout", workItem.CurrentError);
    }

    [Fact]
    public async Task Heartbeat_OnTerminalWorkItem_DoesNotResurrectStatus()
    {
        var workItemId = await SeedRunningTaskAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateTaskStateManager(db, TimeSpan.FromMinutes(15)).CompleteAsync(workItemId);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateTaskStateManager(db, TimeSpan.FromMinutes(15)).HeartbeatAsync(workItemId, "StragglerHeartbeat");
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task ConcurrentHeartbeatAndStaleRecovery_ResolvesWithoutCorruption()
    {
        var workItemId = await SeedRunningTaskAsync();
        var staleTimeout = TimeSpan.FromMilliseconds(50);

        // Make the item already eligible for staleness recovery before the
        // race starts, so both sides have real work to contend over.
        await Task.Delay(100);

        await using var heartbeatDb = new FactoryDbContext(_options);
        await using var recoveryDb = new FactoryDbContext(_options);

        var heartbeatTask = SafeRunAsync(
            () => CreateTaskStateManager(heartbeatDb, staleTimeout).HeartbeatAsync(workItemId, "RacingHeartbeat"));
        var recoveryTask = SafeRunAsync(
            () => CreateTaskStateManager(recoveryDb, staleTimeout).RecoverStaleExecutionsAsync());

        var results = await Task.WhenAll(heartbeatTask, recoveryTask);
        var heartbeatResult = results[0];
        var recoveryResult = results[1];

        // Race outcome is legitimately either side winning — the only
        // invariant is that the loser's failure is a clean, expected
        // ConcurrencyConflictException (existing convention), never an
        // unhandled exception or a corrupted/blank Status.
        Assert.True(
            heartbeatResult is null or ConcurrencyConflictException,
            $"Unexpected heartbeat failure type: {heartbeatResult}");
        Assert.True(
            recoveryResult is null or ConcurrencyConflictException,
            $"Unexpected recovery failure type: {recoveryResult}");

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.True(
            workItem!.Status is nameof(WorkItemStatus.Running)
                or nameof(WorkItemStatus.Repairing) or nameof(WorkItemStatus.Failed),
            $"WorkItem ended in an unexpected status: {workItem.Status}");
    }

    private async Task<Guid> SeedRunningAgentRunAsync(DateTime? updatedAtUtc = null)
    {
        await using var db = new FactoryDbContext(_options);
        var def = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "WorkerAgent",
            AgentType = "Implementation",
            Enabled = true
        };
        db.AgentDefinitions.Add(def);
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = def.Id,
            Status = AgentRunStatus.Running,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    [Fact]
    public async Task RecoverOrphanedRunsAsync_WithCutoff_LeavesHealthyPeerWorkerRunUntouched()
    {
        // Peer worker A is actively running and heartbeating right now (recent UpdatedAtUtc)
        var runId = await SeedRunningAgentRunAsync(updatedAtUtc: DateTime.UtcNow);

        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromSeconds(30);

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        // Worker B starts up and runs recovery sweep
        var recovered = await repo.RecoverOrphanedRunsAsync(cutoffUtc);

        Assert.Equal(0, recovered);

        await using var verifyDb = new FactoryDbContext(_options);
        var run = await new AgentRunRepository(verifyDb).GetByIdAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(AgentRunStatus.Running, run!.Status);
    }

    [Fact]
    public async Task RecoverOrphanedRunsAsync_WithCutoff_RecoversStaleDeadWorkerRun()
    {
        // Interrupted/dead worker run: heartbeat stopped 10 minutes ago
        var runId = await SeedRunningAgentRunAsync(updatedAtUtc: DateTime.UtcNow.AddMinutes(-10));

        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromMinutes(2);

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var recovered = await repo.RecoverOrphanedRunsAsync(cutoffUtc);

        Assert.Equal(1, recovered);

        await using var verifyDb = new FactoryDbContext(_options);
        var run = await new AgentRunRepository(verifyDb).GetByIdAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(AgentRunStatus.Failed, run!.Status);
        Assert.Equal(FailureCategory.Infrastructure, run.FailureCategory);
        Assert.Contains(AgentRunRecovery.OrphanedMarker, run.ErrorMessage);
    }

    [Fact]
    public async Task RecoverOrphanedRunsAsync_ConcurrentWorkerRace_ResolvesSafely()
    {
        // Stale run eligible for recovery
        var runId = await SeedRunningAgentRunAsync(updatedAtUtc: DateTime.UtcNow.AddMinutes(-10));
        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromMinutes(2);

        await using var db1 = new FactoryDbContext(_options);
        await using var db2 = new FactoryDbContext(_options);

        var repo1 = new AgentRunRepository(db1);
        var repo2 = new AgentRunRepository(db2);

        var task1 = SafeRunAsync(() => repo1.RecoverOrphanedRunsAsync(cutoffUtc));
        var task2 = SafeRunAsync(() => repo2.RecoverOrphanedRunsAsync(cutoffUtc));

        var results = await Task.WhenAll(task1, task2);
        Assert.Null(results[0]);
        Assert.Null(results[1]);

        await using var verifyDb = new FactoryDbContext(_options);
        var run = await new AgentRunRepository(verifyDb).GetByIdAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(AgentRunStatus.Failed, run!.Status);
    }

    private static async Task<Exception?> SafeRunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
