using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.13 — OrchestrationRunExecutionEngine now delegates into the
/// authoritative AgentRun pipeline via ITaskDispatchScheduler.DispatchWorkItemAsync
/// instead of independently executing the WorkItem. These tests exercise the
/// engine's own (now small) responsibility: prepare and hand off, never
/// re-implement build/test/repair/review/evaluation.
/// </summary>
public sealed class OrchestrationRunExecutionEngineTests : IDisposable
{
    private sealed class FakeTaskDispatchScheduler : ITaskDispatchScheduler
    {
        private readonly Func<Guid, TaskDispatchOutcome> _respond;

        public FakeTaskDispatchScheduler(Func<Guid, TaskDispatchOutcome> respond) => _respond = respond;

        public int CallCount { get; private set; }

        public Task<SchedulingCycleResult> RunCycleAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by OrchestrationRunExecutionEngine.");

        public Task<TaskDispatchOutcome> DispatchWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_respond(workItemId));
        }
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public OrchestrationRunExecutionEngineTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Run Engine Test",
            Slug = "run-engine-test",
            RootPath = string.Empty,
            Status = ProjectStatus.Implementation,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedWorkItemAsync()
    {
        var id = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        db.WorkItems.Add(new WorkItem
        {
            Id = id,
            ProjectId = _projectId,
            Title = "Do the thing",
            Description = "Implement the feature.",
            Type = WorkItemType.Implementation,
            Status = WorkItemStatus.Ready.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task<OrchestrationRun> SeedRunAsync(Guid workItemId)
    {
        await using var db = new FactoryDbContext(_options);

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            WorkItemId = workItemId,
            Status = OrchestrationRunStatus.Pending
        };

        db.OrchestrationRuns.Add(run);
        await db.SaveChangesAsync();

        return run;
    }

    private OrchestrationRunExecutionEngine MakeEngine(FakeTaskDispatchScheduler scheduler)
    {
        var db = new FactoryDbContext(_options);

        return new OrchestrationRunExecutionEngine(
            new ProjectRepository(db),
            new WorkItemRepository(db),
            new OrchestrationRunRepository(db),
            scheduler,
            NullLogger<OrchestrationRunExecutionEngine>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesAgentRun_AndEndsInExecuting_WithAgentRunIdSet()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);
        var agentRunId = Guid.NewGuid();

        var scheduler = new FakeTaskDispatchScheduler(
            id => new TaskDispatchOutcome(id, true, agentRunId, null, null));

        await MakeEngine(scheduler).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(1, scheduler.CallCount);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Executing, persisted.Status);
        Assert.Equal(agentRunId, persisted.AgentRunId);
        Assert.NotNull(persisted.StartedAtUtc);
    }

    [Theory]
    [InlineData(TaskDispatchSkipReason.NotReady)]
    [InlineData(TaskDispatchSkipReason.NoAgentDefinition)]
    public async Task ExecuteAsync_FailsRun_WhenDispatchIsSkipped(TaskDispatchSkipReason reason)
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var scheduler = new FakeTaskDispatchScheduler(
            id => new TaskDispatchOutcome(id, false, null, reason, $"skipped: {reason}"));

        await MakeEngine(scheduler).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.InfrastructureFailure, persisted.FailureCategory);
        Assert.Contains(reason.ToString(), persisted.ErrorMessage);
        Assert.Null(persisted.AgentRunId);
    }

    [Fact]
    public async Task ExecuteAsync_MarksRunCancelled_WhenCancellationRequestedBeforeDispatch()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var scheduler = new FakeTaskDispatchScheduler(
            _ => throw new InvalidOperationException("Must not dispatch once cancelled."));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await MakeEngine(scheduler).ExecuteAsync(run, cts.Token);

        Assert.Equal(0, scheduler.CallCount);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Cancelled, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.Cancelled, persisted.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRun_WhenProjectNoLongerExists()
    {
        // OrchestrationRun deliberately has no FK to Project (see
        // FactoryDbContext) — its own ProjectId can point at a Project that
        // no longer exists (WorkItemId still targets a real, seeded WorkItem
        // under the real project, since only run.ProjectId is under test here).
        var orphanProjectId = Guid.NewGuid();
        var workItemId = await SeedWorkItemAsync();

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = orphanProjectId,
            WorkItemId = workItemId,
            Status = OrchestrationRunStatus.Pending
        };

        await using (var db = new FactoryDbContext(_options))
        {
            db.OrchestrationRuns.Add(run);
            await db.SaveChangesAsync();
        }

        var scheduler = new FakeTaskDispatchScheduler(
            _ => throw new InvalidOperationException("Must not dispatch when Project is missing."));

        await MakeEngine(scheduler).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(0, scheduler.CallCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var persisted = await verifyDb.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.InfrastructureFailure, persisted.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRun_WhenWorkItemNoLongerExists()
    {
        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            WorkItemId = Guid.NewGuid(),
            Status = OrchestrationRunStatus.Pending
        };

        await using (var db = new FactoryDbContext(_options))
        {
            db.OrchestrationRuns.Add(run);
            await db.SaveChangesAsync();
        }

        var scheduler = new FakeTaskDispatchScheduler(
            _ => throw new InvalidOperationException("Must not dispatch when WorkItem is missing."));

        await MakeEngine(scheduler).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(0, scheduler.CallCount);

        await using var verifyDb = new FactoryDbContext(_options);
        var persisted = await verifyDb.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.InfrastructureFailure, persisted.FailureCategory);
    }
}
