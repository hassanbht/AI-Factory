using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Proves the real AutonomousCodingOrchestrator, wired to a real
/// OrchestrationRunTraceService/OrchestrationRunRepository (not fakes),
/// actually produces OrchestrationRunStep rows for real stage boundaries —
/// Phase 17.2 scenarios 8-10 (orchestration integration for success/failure,
/// and a broken trace service never swallowing the real business outcome).
/// </summary>
public sealed class OrchestrationInstrumentationTests : IDisposable
{
    private sealed class FakeValidationEngine : IValidationEngine
    {
        private readonly ValidationResult _result;

        public FakeValidationEngine(ValidationResult result) => _result = result;

        public static ValidationResult SuccessResult { get; } = new(
            true, true, "build ok", true, "tests ok", Array.Empty<ValidationFailure>());

        public static ValidationResult BuildFailureResult { get; } = new(
            false, false, "build broke", null, null,
            new[] { new ValidationFailure("Build", "compile error") });

        public Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class FakeCodeReviewer : ICodeReviewer
    {
        public static ReviewResult NoFindingsResult { get; } = new(Guid.Empty, false, Array.Empty<ReviewFinding>());

        public Task<ReviewResult> ReviewAsync(ReviewContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(NoFindingsResult);
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult> _respond;

        public FakeAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> respond) => _respond = respond;

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_respond(request));
    }

    /// <summary>
    /// Wraps a real repository but breaks step persistence specifically —
    /// simulates the trace's own storage failing while everything else
    /// (attempts, progress events, the run itself) keeps working normally.
    /// </summary>
    private sealed class StepPersistenceFailsRepository : IOrchestrationRunRepository
    {
        private readonly IOrchestrationRunRepository _inner;

        public StepPersistenceFailsRepository(IOrchestrationRunRepository inner) => _inner = inner;

        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trace storage is unavailable.");

        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(
            Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trace storage is unavailable.");

        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Trace storage is unavailable.");

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(run, cancellationToken);

        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default)
            => _inner.GetByIdAsync(runId, cancellationToken);

        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default)
            => _inner.GetActiveByWorkItemIdAsync(workItemId, cancellationToken);

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
            => _inner.TryClaimNextPendingAsync(cancellationToken);

        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(run, cancellationToken);

        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default)
            => _inner.AddAttemptAsync(attempt, cancellationToken);

        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default)
            => _inner.AddProgressEventAsync(progressEvent, cancellationToken);

        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default)
            => _inner.RequestCancellationAsync(runId, cancellationToken);

        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => _inner.RenewLeaseAsync(runId, leaseDuration, cancellationToken);

        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default)
            => _inner.RecoverStaleLeasesAsync(cancellationToken);
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public OrchestrationInstrumentationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Instrumentation Test",
            Slug = "instrumentation-test",
            Status = ProjectStatus.Implementation,
            RootPath = Path.Combine(Path.GetTempPath(), "ai-factory-instrumentation-tests", Guid.NewGuid().ToString("N")),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private static ModelRoutingOptions MakeModelRoutingOptions() => new()
    {
        Models = new List<ModelCapability>
        {
            new()
            {
                ModelId = "test-model",
                Provider = "test-provider",
                ReasoningStrength = 3,
                MaxContextTokens = 100_000,
                CostTier = 2,
                IsCodingOptimized = true,
                IsAvailable = true
            }
        },
        CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["coding"] = new List<string> { "test-model" }
        }
    };

    private async Task<Guid> SeedWorkItemWithActiveRunAsync()
    {
        var workItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);

        db.WorkItems.Add(new WorkItem
        {
            Id = workItemId,
            ProjectId = _projectId,
            Title = "Implement feature",
            Description = "Do the thing.",
            Type = WorkItemType.Implementation,
            Status = WorkItemStatus.Draft.ToString(),
            Priority = "Normal",
            MaxAttempts = 3,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            Id = runId,
            ProjectId = _projectId,
            WorkItemId = workItemId,
            Status = OrchestrationRunStatus.Executing
        });

        await db.SaveChangesAsync();

        return workItemId;
    }

    private AutonomousCodingOrchestrator MakeOrchestrator(
        FactoryDbContext db,
        FakeAgentExecutor executor,
        IOrchestrationRunTraceService traceService,
        ValidationResult? validationResult = null)
    {
        var workItemRepository = new WorkItemRepository(db);
        var resolver = new TaskDependencyResolver();
        var scheduler = new WorkItemTaskScheduler(workItemRepository, resolver);
        var taskStateManager = new TaskStateManager(db);
        var agentRunRepository = new AgentRunRepository(db);
        var artifactRecorder = new FileSystemArtifactRecorder(db);
        var contextProvider = new LocalContextProvider(db);
        var modelRouter = new ModelRouter(MakeModelRoutingOptions());
        var projectRepository = new ProjectRepository(db);
        var validationEngine = new FakeValidationEngine(validationResult ?? FakeValidationEngine.SuccessResult);
        var validationRunRepository = new ValidationRunRepository(db);
        var workflowEngine = new WorkflowEngine(new WorkflowStateStore(db), new WorkflowTransitionValidator());
        var approvalService = new ApprovalService(new ApprovalRepository(db), workflowEngine);
        var codeReviewer = new FakeCodeReviewer();
        var artifactRepository = new ArtifactRepository(db);
        var reviewContextBuilder = new ReviewContextBuilder(contextProvider, artifactRepository, validationRunRepository);
        var reviewRunRepository = new ReviewRunRepository(db);
        var evaluationEngine = new EvaluationEngine(validationRunRepository, reviewRunRepository, new ApprovalRepository(db));
        var evaluationRunRepository = new EvaluationRunRepository(db);
        var learningService = new LearningService(new LearningStore(db));

        return new AutonomousCodingOrchestrator(
            scheduler,
            taskStateManager,
            executor,
            agentRunRepository,
            artifactRecorder,
            contextProvider,
            modelRouter,
            projectRepository,
            validationEngine,
            validationRunRepository,
            approvalService,
            new RepairOptions(),
            codeReviewer,
            reviewContextBuilder,
            reviewRunRepository,
            new ReviewOptions(),
            evaluationEngine,
            evaluationRunRepository,
            learningService,
            workItemRepository,
            artifactRepository,
            new ExecutionLimitsOptions(),
            null,
            null,
            traceService,
            null,
            NullLogger<AutonomousCodingOrchestrator>.Instance);
    }

    [Fact]
    public async Task SuccessfulRun_ProducesSucceededStepsInTrace()
    {
        var workItemId = await SeedWorkItemWithActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var runRepository = new OrchestrationRunRepository(db);
        var traceService = new OrchestrationRunTraceService(runRepository, NullLogger<OrchestrationRunTraceService>.Instance);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "done", null, 10, "/workspace"));
        var orchestrator = MakeOrchestrator(db, executor, traceService);

        var outcome = await orchestrator.ExecuteSpecificWorkItemAsync(workItemId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);

        var run = await db.OrchestrationRuns.Include(r => r.Steps).SingleAsync(r => r.WorkItemId == workItemId);

        var agentStep = Assert.Single(run.Steps, s => s.Stage == OrchestrationStepKind.AgentExecution);
        Assert.Equal(OrchestrationStepStatus.Succeeded, agentStep.Status);
        Assert.NotNull(agentStep.CompletedAtUtc);

        var buildStep = Assert.Single(run.Steps, s => s.Stage == OrchestrationStepKind.Build);
        Assert.Equal(OrchestrationStepStatus.Succeeded, buildStep.Status);

        var reviewStep = Assert.Single(run.Steps, s => s.Stage == OrchestrationStepKind.CodeReview);
        Assert.Equal(OrchestrationStepStatus.Succeeded, reviewStep.Status);
    }

    [Fact]
    public async Task BuildFailure_ProducesFailedStepInTrace_WithFailureMessage()
    {
        var workItemId = await SeedWorkItemWithActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var runRepository = new OrchestrationRunRepository(db);
        var traceService = new OrchestrationRunTraceService(runRepository, NullLogger<OrchestrationRunTraceService>.Instance);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "done", null, 10, "/workspace"));
        var orchestrator = MakeOrchestrator(db, executor, traceService, FakeValidationEngine.BuildFailureResult);

        await orchestrator.ExecuteSpecificWorkItemAsync(workItemId);

        var run = await db.OrchestrationRuns.Include(r => r.Steps).SingleAsync(r => r.WorkItemId == workItemId);

        var buildStep = Assert.Single(run.Steps, s => s.Stage == OrchestrationStepKind.Build);
        Assert.Equal(OrchestrationStepStatus.Failed, buildStep.Status);
        Assert.Equal("BuildFailure", buildStep.FailureType);
        Assert.False(string.IsNullOrWhiteSpace(buildStep.FailureMessage));

        // Tests never ran because the build failed first — a real skip, not a fabricated one.
        var testStep = Assert.Single(run.Steps, s => s.Stage == OrchestrationStepKind.Test);
        Assert.Equal(OrchestrationStepStatus.Skipped, testStep.Status);
    }

    [Fact]
    public async Task TraceStoragePersistenceFailure_DoesNotSwallowTheRealOrchestrationOutcome()
    {
        var workItemId = await SeedWorkItemWithActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var brokenRepository = new StepPersistenceFailsRepository(new OrchestrationRunRepository(db));
        var traceService = new OrchestrationRunTraceService(brokenRepository, NullLogger<OrchestrationRunTraceService>.Instance);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(false, null, "Agent blew up.", 5, null));
        var orchestrator = MakeOrchestrator(db, executor, traceService);

        // Step persistence throws on every call — OrchestrationRunTraceService
        // must catch and log that internally (its own documented contract,
        // same as OrchestrationProgressReporter) so this call still returns
        // the real (agent-failure) outcome instead of throwing or hanging.
        var outcome = await orchestrator.ExecuteSpecificWorkItemAsync(workItemId);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Equal("Agent blew up.", outcome.Error);
    }
}
