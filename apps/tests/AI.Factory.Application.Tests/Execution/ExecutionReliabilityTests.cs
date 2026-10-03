using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
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

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase M — crash recovery, orphan cleanup and lifecycle-race regressions found by the
/// Archon-vs-Factory benchmark (bench/archon-vs-factory/PROTOCOL.md, factory-T3-r1): a killed Worker
/// left its AgentRun Running and its container working the workspace for ~15 minutes next to the
/// replacement run. Real SQLite repositories, a recording fake runtime — no process or container.
/// </summary>
public sealed class ExecutionReliabilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ExecutionReliabilityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Records every runtime call in one shared, ordered journal.</summary>
    private sealed class RecordingRuntime : IContainerLifecycleManager
    {
        private readonly List<string> _journal;

        public RecordingRuntime(List<string> journal) => _journal = journal;

        public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            lock (_journal)
            {
                _journal.Add($"cleanup:{agentRunId}");
            }

            return Task.CompletedTask;
        }

        public Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>Records the run's status at the moment the WorkItem sync sees it.</summary>
    private sealed class JournalingSync : IAgentRunWorkItemSync
    {
        private readonly IAgentRunWorkItemSync _inner;
        private readonly List<string> _journal;

        public JournalingSync(IAgentRunWorkItemSync inner, List<string> journal)
        {
            _inner = inner;
            _journal = journal;
        }

        public Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default)
        {
            lock (_journal)
            {
                _journal.Add($"sync:{agentRun.Id}:{agentRun.Status}");
            }

            return _inner.SyncAsync(agentRun, cancellationToken);
        }
    }

    private sealed class PassingGate : IVerificationGate
    {
        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "ok", 0, TimeSpan.Zero));

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "ok", 0, TimeSpan.Zero));
    }

    private sealed class CompletingProcessor : IWorkItemCompletionProcessor
    {
        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
    }

    private static AgentRunOrphanRecovery CreateRecovery(FactoryDbContext db, List<string> journal) => new(
        new AgentRunRepository(db),
        new JournalingSync(
            new AgentRunWorkItemSync(
                new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
                new PassingGate(), new CompletingProcessor(), NullLogger<AgentRunWorkItemSync>.Instance),
            journal),
        new IContainerLifecycleManager[] { new RecordingRuntime(journal) },
        new RecoveryOptions { OrphanHeartbeatTimeout = TimeSpan.FromMinutes(2) },
        NullLogger<AgentRunOrphanRecovery>.Instance);

    /// <summary>A WorkItem Running with one Running AgentRun whose last heartbeat was <paramref name="heartbeatAge"/> ago.</summary>
    private async Task<(Guid WorkItemId, Guid AgentRunId)> SeedExecutingAsync(TimeSpan heartbeatAge)
    {
        await using var db = new FactoryDbContext(_options);
        var projects = new ProjectRepository(db);
        var project = await new ProjectService(projects).CreateAsync($"Recovery {Guid.NewGuid():N}", "d");
        project.Status = ProjectStatus.Implementation;
        await projects.SaveChangesAsync();

        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);

        var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"impl-{Guid.NewGuid():N}", AgentType = "Implementation", SystemPrompt = "x"
        });

        var run = await new AgentRunRepository(db).CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id, ProjectId = project.Id, WorkItemId = item.Id
        });
        run.TransitionTo(AgentRunStatus.Queued);
        run.TransitionTo(AgentRunStatus.Running);
        run.UpdatedAtUtc = DateTime.UtcNow - heartbeatAge;
        await db.SaveChangesAsync();

        return (item.Id, run.Id);
    }

    private async Task<(AgentRun Run, WorkItem WorkItem)> LoadAsync(Guid agentRunId, Guid workItemId)
    {
        await using var db = new FactoryDbContext(_options);
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == agentRunId);
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(x => x.Id == workItemId);
        return (run, workItem);
    }

    [Fact]
    public async Task WorkerCrash_OrphanIsTerminatedBeforeItsWorkItemIsReleasedForRetry()
    {
        var (workItemId, runId) = await SeedExecutingAsync(TimeSpan.FromMinutes(10));
        var journal = new List<string>();

        await using (var db = new FactoryDbContext(_options))
        {
            Assert.Equal(1, await CreateRecovery(db, journal).RecoverAsync());
        }

        // Order is the guarantee: runtime gone, run terminal, only then the WorkItem may be retried.
        Assert.Equal(new[] { $"cleanup:{runId}", $"sync:{runId}:Failed" }, journal);

        var (run, workItem) = await LoadAsync(runId, workItemId);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(FailureCategory.Infrastructure, run.FailureCategory);
        Assert.True(AgentRunRecovery.WasOrphaned(run));
        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
    }

    [Fact]
    public async Task HeartbeatingRun_OfALiveWorker_IsNeverTouched()
    {
        var (workItemId, runId) = await SeedExecutingAsync(TimeSpan.FromSeconds(20));
        var journal = new List<string>();

        await using (var db = new FactoryDbContext(_options))
        {
            Assert.Equal(0, await CreateRecovery(db, journal).RecoverAsync());
        }

        Assert.Empty(journal);
        var (run, workItem) = await LoadAsync(runId, workItemId);
        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.Equal(WorkItemStatus.Running.ToString(), workItem.Status);
    }

    [Fact]
    public async Task RepeatedWorkerRestarts_RecoverEachOrphanExactlyOnce()
    {
        var (workItemId, runId) = await SeedExecutingAsync(TimeSpan.FromMinutes(10));
        var journal = new List<string>();

        for (var restart = 0; restart < 3; restart++)
        {
            await using var db = new FactoryDbContext(_options);
            await CreateRecovery(db, journal).RecoverAsync();
        }

        Assert.Single(journal, e => e.StartsWith("sync:", StringComparison.Ordinal));
        var (_, workItem) = await LoadAsync(runId, workItemId);
        Assert.Equal(1, workItem.AttemptCount);
    }

    [Fact]
    public async Task OverlappingWorkers_RecoveringConcurrently_ProduceExactlyOneRetry()
    {
        var (workItemId, runId) = await SeedExecutingAsync(TimeSpan.FromMinutes(10));
        var journal = new List<string>();

        await using var db1 = new FactoryDbContext(_options);
        await using var db2 = new FactoryDbContext(_options);
        var results = await Task.WhenAll(
            CreateRecovery(db1, journal).RecoverAsync(),
            CreateRecovery(db2, journal).RecoverAsync());

        // Both may terminate the runtime (idempotent); exactly one recovers the run and syncs it.
        Assert.Equal(1, results.Sum());
        Assert.Single(journal, e => e.StartsWith("sync:", StringComparison.Ordinal));
        var (_, workItem) = await LoadAsync(runId, workItemId);
        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);
    }

    private sealed class ThrowingRuntime : IContainerLifecycleManager
    {
        private readonly Guid _failFor;

        public ThrowingRuntime(Guid failFor) => _failFor = failFor;

        public Task EnsureCleanupAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            agentRunId == _failFor ? throw new AggregateException("tree kill failed") : Task.CompletedTask;

        public Task<int> SweepOrphanContainersAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("docker unavailable");
    }

    /// <summary>Phase M review — one failing candidate (or runtime sweep) must not stop the others' recovery.</summary>
    [Fact]
    public async Task OneFailingCandidate_DoesNotStarveTheOthers()
    {
        var (_, first) = await SeedExecutingAsync(TimeSpan.FromMinutes(20));
        var (secondItem, second) = await SeedExecutingAsync(TimeSpan.FromMinutes(10));

        await using (var db = new FactoryDbContext(_options))
        {
            var recovery = new AgentRunOrphanRecovery(
                new AgentRunRepository(db),
                new AgentRunWorkItemSync(
                    new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
                    new PassingGate(), new CompletingProcessor(), NullLogger<AgentRunWorkItemSync>.Instance),
                new IContainerLifecycleManager[] { new ThrowingRuntime(first) },
                new RecoveryOptions { OrphanHeartbeatTimeout = TimeSpan.FromMinutes(2) },
                NullLogger<AgentRunOrphanRecovery>.Instance);

            Assert.Equal(2, await recovery.RecoverAsync());
        }

        var (run, workItem) = await LoadAsync(second, secondItem);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(WorkItemStatus.Ready.ToString(), workItem.Status);
    }

    [Fact]
    public async Task RunThatHeartbeatsAfterBeingListed_IsLeftRunning()
    {
        var (_, runId) = await SeedExecutingAsync(TimeSpan.FromMinutes(10));
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);

        await using (var heartbeat = new FactoryDbContext(_options))
        {
            await new AgentRunRepository(heartbeat).HeartbeatAsync(runId);
        }

        await using var db = new FactoryDbContext(_options);
        Assert.Null(await new AgentRunRepository(db).TryRecoverOrphanedRunAsync(runId, cutoff, "test"));
        Assert.Equal(AgentRunStatus.Running, (await db.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId)).Status);
    }

    /// <summary>
    /// A completion that loses the race to a cancellation (or to orphan recovery) must not overwrite it:
    /// AgentRunRepository.UpdateAsync used to resolve every concurrency conflict by re-applying the
    /// late writer's values.
    /// </summary>
    [Fact]
    public async Task LateCompletion_NeverTurnsACancelledRunIntoSucceeded()
    {
        var (_, runId) = await SeedExecutingAsync(TimeSpan.Zero);

        await using var orchestratorDb = new FactoryDbContext(_options);
        var orchestratorRepo = new AgentRunRepository(orchestratorDb);
        var inFlight = (await orchestratorRepo.GetByIdAsync(runId))!;

        await using (var cancelDb = new FactoryDbContext(_options))
        {
            var cancelled = await cancelDb.AgentRuns.SingleAsync(x => x.Id == runId);
            cancelled.TransitionTo(AgentRunStatus.Cancelled);
            await cancelDb.SaveChangesAsync();
        }

        inFlight.TransitionTo(AgentRunStatus.Succeeded);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => orchestratorRepo.UpdateAsync(inFlight));

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(AgentRunStatus.Cancelled, (await verify.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId)).Status);
    }

    [Fact]
    public async Task BenignHeartbeatCollision_IsStillMerged()
    {
        var (_, runId) = await SeedExecutingAsync(TimeSpan.Zero);

        await using var orchestratorDb = new FactoryDbContext(_options);
        var orchestratorRepo = new AgentRunRepository(orchestratorDb);
        var inFlight = (await orchestratorRepo.GetByIdAsync(runId))!;

        await using (var heartbeatDb = new FactoryDbContext(_options))
        {
            await new AgentRunRepository(heartbeatDb).HeartbeatAsync(runId);
        }

        inFlight.TransitionTo(AgentRunStatus.Succeeded);
        await orchestratorRepo.UpdateAsync(inFlight);

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(AgentRunStatus.Succeeded, (await verify.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId)).Status);
    }

    [Fact]
    public async Task RepeatedCancellation_ChangesNothingAfterTheFirst()
    {
        Guid runId;
        var metrics = new OperationalMetrics();

        await using (var db = new FactoryDbContext(_options))
        {
            var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
            {
                Name = "cancel-def", AgentType = "Implementation", SystemPrompt = "x"
            });
            var run = await new AgentRunRepository(db).CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
            run.TransitionTo(AgentRunStatus.Queued);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        AgentRunService Service(FactoryDbContext db) => new(
            new AgentRunRepository(db), new AgentDefinitionRepository(db), metrics, NullLogger<AgentRunService>.Instance);

        await using (var db = new FactoryDbContext(_options))
        {
            await Service(db).CancelAsync(runId);
        }

        // The existing API contract answers a repeat with 409; what matters is that it changes nothing.
        for (var i = 0; i < 2; i++)
        {
            await using var db = new FactoryDbContext(_options);
            await Assert.ThrowsAsync<ConflictException>(() => Service(db).CancelAsync(runId));
        }

        await using var verify = new FactoryDbContext(_options);
        var reloaded = await verify.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
        Assert.Equal(AgentRunStatus.Cancelled, reloaded.Status);
        Assert.Equal(1, await verify.AgentRuns.CountAsync());
        Assert.Equal(1, metrics.GetSnapshot().AgentRunsCancelled);
    }
}
