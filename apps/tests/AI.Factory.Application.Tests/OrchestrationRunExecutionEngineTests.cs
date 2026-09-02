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
/// Exercises OrchestrationRunExecutionEngine with test doubles for
/// ITaskPlanner/IAutonomousCodingOrchestrator — no real Hermes server
/// involved (see Phase 17 Section 20/21: application-level tests use test
/// doubles; a real Hermes round-trip is a separate, opt-in smoke test).
/// </summary>
public sealed class OrchestrationRunExecutionEngineTests : IDisposable
{
    private sealed class FakeTaskPlanner : ITaskPlanner
    {
        private readonly Func<TaskPlanRequest, TaskPlan> _respond;

        public FakeTaskPlanner(Func<TaskPlanRequest, TaskPlan> respond)
        {
            _respond = respond;
        }

        public int CallCount { get; private set; }

        public Task<TaskPlan> CreatePlanAsync(TaskPlanRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_respond(request));
        }

        public static TaskPlan ValidPlan(Guid projectId) => new(
            projectId,
            new[]
            {
                new PlannedTask(
                    Guid.NewGuid(), "Implement the change", "Do the work.",
                    WorkItemType.Implementation, "Normal", Array.Empty<Guid>(), null, TaskComplexity.Standard)
            });
    }

    /// <summary>
    /// Simulates AutonomousCodingOrchestrator's real effect on WorkItem
    /// state (Status/AttemptCount) without needing a real agent/build/test
    /// pipeline — the engine re-fetches the WorkItem after every call, so
    /// the fake must persist real state for that to mean anything.
    /// </summary>
    private sealed class FakeOrchestrator : IAutonomousCodingOrchestrator
    {
        private readonly DbContextOptions<FactoryDbContext> _dbOptions;
        private readonly Func<int, (WorkItemStatus Status, WorkItemExecutionOutcome Outcome)> _respond;
        private readonly Action<int>? _onBeforeRespond;

        public FakeOrchestrator(
            DbContextOptions<FactoryDbContext> dbOptions,
            Func<int, (WorkItemStatus, WorkItemExecutionOutcome)> respond,
            Action<int>? onBeforeRespond = null)
        {
            _dbOptions = dbOptions;
            _respond = respond;
            _onBeforeRespond = onBeforeRespond;
        }

        public int CallCount { get; private set; }

        public Task<WorkItemExecutionOutcome?> ExecuteNextReadyTaskAsync(
            Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by OrchestrationRunExecutionEngine.");

        public async Task<WorkItemExecutionOutcome?> ExecuteSpecificWorkItemAsync(
            Guid workItemId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            _onBeforeRespond?.Invoke(CallCount);
            var (status, outcome) = _respond(CallCount);

            await using var db = new FactoryDbContext(_dbOptions);
            var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
            item.Status = status.ToString();
            item.AttemptCount = CallCount;
            item.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return outcome;
        }
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly OrchestrationOptions _orchestrationOptions;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly string _workspaceRoot;

    public OrchestrationRunExecutionEngineTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-orch-run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
        _orchestrationOptions = new OrchestrationOptions { WorkspaceRoot = _workspaceRoot, MaxAttempts = 3 };

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

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private async Task<Guid> SeedWorkItemAsync(int maxAttempts = 3)
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
            Status = WorkItemStatus.Draft.ToString(),
            MaxAttempts = maxAttempts,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task<OrchestrationRun> SeedRunAsync(Guid workItemId, int maxAttempts = 3)
    {
        await using var db = new FactoryDbContext(_options);

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            WorkItemId = workItemId,
            Status = OrchestrationRunStatus.Pending,
            MaxAttempts = maxAttempts
        };

        db.OrchestrationRuns.Add(run);
        await db.SaveChangesAsync();

        return run;
    }

    private OrchestrationRunExecutionEngine MakeEngine(FakeTaskPlanner planner, FakeOrchestrator orchestrator)
    {
        var db = new FactoryDbContext(_options);

        return new OrchestrationRunExecutionEngine(
            new ProjectRepository(db),
            new WorkItemRepository(db),
            planner,
            orchestrator,
            new OrchestrationRunRepository(db),
            _orchestrationOptions,
            NullLogger<OrchestrationRunExecutionEngine>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_CompletesRun_OnFirstAttemptSuccess()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, _ =>
            (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.Include(x => x.Attempts).SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Completed, persisted.Status);
        Assert.Single(persisted.Attempts);
        Assert.NotNull(persisted.WorkspacePath);
        Assert.StartsWith(Path.GetFullPath(_workspaceRoot), persisted.WorkspacePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversFromBuildFailure_ThenSucceeds()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, callCount => callCount == 1
            ? (WorkItemStatus.Repairing, new WorkItemExecutionOutcome(workItemId, false, "build failed", PipelineFailureCategory.Build))
            : (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.Include(x => x.Attempts).SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Completed, persisted.Status);
        Assert.Equal(2, persisted.Attempts.Count);
        Assert.Equal(OrchestrationFailureCategory.BuildFailure, persisted.Attempts.First(a => a.AttemptNumber == 1).FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversFromTestFailure_ThenSucceeds()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, callCount => callCount == 1
            ? (WorkItemStatus.Repairing, new WorkItemExecutionOutcome(workItemId, false, "test failed", PipelineFailureCategory.Test))
            : (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.Include(x => x.Attempts).SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Completed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.TestFailure, persisted.Attempts.First(a => a.AttemptNumber == 1).FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRun_WhenMaxAttemptsExceeded()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 2);
        var run = await SeedRunAsync(workItemId, maxAttempts: 2);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, callCount => callCount < 2
            ? (WorkItemStatus.Repairing, new WorkItemExecutionOutcome(workItemId, false, "still failing", PipelineFailureCategory.Build))
            : (WorkItemStatus.Failed, new WorkItemExecutionOutcome(workItemId, false, "attempts exhausted", PipelineFailureCategory.Build)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.Include(x => x.Attempts).SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(2, persisted.Attempts.Count);
        Assert.Equal(OrchestrationFailureCategory.BuildFailure, persisted.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_FailsRun_WithoutCallingOrchestrator_WhenPlanIsInvalid()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(_ => throw new InvalidTaskPlanException("empty plan"));
        var orchestrator = new FakeOrchestrator(_options, _ =>
            (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.PlanningFailure, persisted.FailureCategory);
        Assert.Equal(0, orchestrator.CallCount); // Do not call Hermes when the plan is invalid.
    }

    [Fact]
    public async Task ExecuteAsync_FailsRun_WhenAgentExecutionFailsImmediately()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);
        var run = await SeedRunAsync(workItemId, maxAttempts: 1);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, _ =>
            (WorkItemStatus.Failed, new WorkItemExecutionOutcome(workItemId, false, "agent crashed", PipelineFailureCategory.AgentExecution)));

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Failed, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.AgentFailure, persisted.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_MarksRunCancelled_WhenCancellationRequested()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(_options, _ =>
            (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, cts.Token);

        await using var db = new FactoryDbContext(_options);
        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.Cancelled, persisted.Status);
        Assert.Equal(OrchestrationFailureCategory.Cancelled, persisted.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_MovesToAwaitingApproval_WhenProjectEntersHumanReview()
    {
        var workItemId = await SeedWorkItemAsync();
        var run = await SeedRunAsync(workItemId);

        var planner = new FakeTaskPlanner(req => FakeTaskPlanner.ValidPlan(req.ProjectId));
        var orchestrator = new FakeOrchestrator(
            _options,
            _ => (WorkItemStatus.Completed, new WorkItemExecutionOutcome(workItemId, true, null)),
            onBeforeRespond: _ =>
            {
                // Simulate MaybeRequestFinalApprovalAsync's real effect:
                // moving the project to HumanReview once the WorkItem completes.
                using var db = new FactoryDbContext(_options);
                var project = db.Projects.Single(x => x.Id == _projectId);
                project.Status = ProjectStatus.HumanReview;
                db.SaveChanges();
            });

        await MakeEngine(planner, orchestrator).ExecuteAsync(run, CancellationToken.None);

        await using var verifyDb = new FactoryDbContext(_options);
        var persisted = await verifyDb.OrchestrationRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Equal(OrchestrationRunStatus.AwaitingApproval, persisted.Status);
    }
}
