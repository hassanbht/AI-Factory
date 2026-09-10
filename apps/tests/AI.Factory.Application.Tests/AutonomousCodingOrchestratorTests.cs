using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Exceptions;
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

public sealed class AutonomousCodingOrchestratorTests : IDisposable
{
    private sealed class FakeValidationEngine : IValidationEngine
    {
        private readonly ValidationResult _result;

        public FakeValidationEngine(ValidationResult result)
        {
            _result = result;
        }

        public static ValidationResult SuccessResult { get; } = new(
            true, true, "build ok", true, "tests ok", Array.Empty<ValidationFailure>());

        public Task<ValidationResult> ValidateAsync(
            ValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeCodeReviewer : ICodeReviewer
    {
        private readonly ReviewResult _result;

        public FakeCodeReviewer(ReviewResult result)
        {
            _result = result;
        }

        public static ReviewResult NoFindingsResult { get; } =
            new(Guid.Empty, false, Array.Empty<ReviewFinding>());

        public Task<ReviewResult> ReviewAsync(
            ReviewContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult> _respond;

        public FakeAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> respond)
        {
            _respond = respond;
        }

        public int CallCount { get; private set; }

        public AgentExecutionRequest? LastRequest { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public AutonomousCodingOrchestratorTests()
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
            Name = "Orchestrator Test",
            Slug = "orchestrator-test",
            Status = ProjectStatus.Implementation,
            RootPath = Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests", Guid.NewGuid().ToString("N")),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static ModelRoutingOptions MakeModelRoutingOptions()
    {
        return new ModelRoutingOptions
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
    }

    private async Task<Guid> SeedWorkItemAsync(int maxAttempts = 3, string priority = "Normal")
    {
        var id = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);

        db.WorkItems.Add(new WorkItem
        {
            Id = id,
            ProjectId = _projectId,
            Title = "Implement feature",
            Description = "Do the thing.",
            Type = WorkItemType.Implementation,
            Status = WorkItemStatus.Draft.ToString(),
            Priority = priority,
            MaxAttempts = maxAttempts,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        return id;
    }

    private AutonomousCodingOrchestrator MakeOrchestrator(
        FactoryDbContext db,
        FakeAgentExecutor executor,
        ValidationResult? validationResult = null,
        RepairOptions? repairOptions = null,
        ReviewResult? reviewResult = null,
        ReviewOptions? reviewOptions = null,
        IAuditLogger? auditLogger = null)
    {
        var workItemRepository = new WorkItemRepository(db);
        var resolver = new TaskDependencyResolver();
        var scheduler = new WorkItemTaskScheduler(workItemRepository, resolver);
        var taskStateManager = new TaskStateManager(db);
        var agentRunRepository = new HermesExecutionRepository(db);
        var artifactRecorder = new FileSystemArtifactRecorder(db);
        var contextProvider = new LocalContextProvider(db);
        var modelRouter = new ModelRouter(MakeModelRoutingOptions());
        var projectRepository = new ProjectRepository(db);
        var validationEngine = new FakeValidationEngine(validationResult ?? FakeValidationEngine.SuccessResult);
        var validationRunRepository = new ValidationRunRepository(db);
        var workflowEngine = new WorkflowEngine(new WorkflowStateStore(db), new WorkflowTransitionValidator());
        var approvalService = new ApprovalService(new ApprovalRepository(db), workflowEngine);
        var codeReviewer = new FakeCodeReviewer(reviewResult ?? FakeCodeReviewer.NoFindingsResult);
        var artifactRepository = new ArtifactRepository(db);
        var reviewContextBuilder = new ReviewContextBuilder(contextProvider, artifactRepository, validationRunRepository);
        var reviewRunRepository = new ReviewRunRepository(db);
        var approvalRepository = new ApprovalRepository(db);
        var evaluationEngine = new EvaluationEngine(validationRunRepository, reviewRunRepository, approvalRepository);
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
            repairOptions ?? new RepairOptions(),
            codeReviewer,
            reviewContextBuilder,
            reviewRunRepository,
            reviewOptions ?? new ReviewOptions(),
            evaluationEngine,
            evaluationRunRepository,
            learningService,
            workItemRepository,
            artifactRepository,
            new ExecutionLimitsOptions(),
            auditLogger,
            null,
            null,
            null,
            NullLogger<AutonomousCodingOrchestrator>.Instance);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_ReturnsNull_WhenNothingIsReady()
    {
        // No WorkItems seeded at all for this project.
        var executor = new FakeAgentExecutor(_ => throw new InvalidOperationException("should not be called"));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.Null(outcome);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_CompletesTask_AndRecordsArtifacts_OnSuccess()
    {
        var workItemId = await SeedWorkItemAsync();

        var workspaceDir = Path.Combine(Path.GetTempPath(), "ai-factory-orchestrator-tests", "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        await File.WriteAllTextAsync(Path.Combine(workspaceDir, "output.cs"), "class Generated {}");

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "Implemented successfully.", null, 42, workspaceDir));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.Equal(workItemId, outcome.WorkItemId);
        Assert.Equal(1, executor.CallCount);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);

        var agentRun = await db.HermesExecutions.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.True(agentRun.Success);
        Assert.Equal("Implemented successfully.", agentRun.Response);
        Assert.Equal("test-model", agentRun.Model);
        Assert.NotNull(agentRun.CompletedAtUtc);

        var artifact = await db.Artifacts.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.Equal("output.cs", artifact.Name);
        Assert.Equal(_projectId, artifact.ProjectId);

        Directory.Delete(workspaceDir, recursive: true);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_TransitionsToRepairing_OnFailureWithAttemptsRemaining()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "build failed", 10));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Equal("build failed", outcome.Error);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);
        Assert.Equal(1, workItem.AttemptCount);

        var agentRun = await db.HermesExecutions.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.False(agentRun.Success);
        Assert.Equal("build failed", agentRun.Error);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_TransitionsToFailed_OnFailureWithAttemptsExhausted()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "build failed again", 10));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);
    }

    [Fact]
    public async Task ExecuteSpecificWorkItemAsync_FeedsRepairContext_IntoRetryPrompt()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var failingExecutor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "compile error: missing semicolon", 5));

        await using (var db1 = new FactoryDbContext(_options))
        {
            var orchestrator1 = MakeOrchestrator(db1, failingExecutor);
            await orchestrator1.ExecuteSpecificWorkItemAsync(workItemId);
        }

        var retryExecutor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "fixed it", null, 5));

        await using var db2 = new FactoryDbContext(_options);
        var orchestrator2 = MakeOrchestrator(db2, retryExecutor);
        var outcome = await orchestrator2.ExecuteSpecificWorkItemAsync(workItemId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.NotNull(retryExecutor.LastRequest);
        Assert.Contains(
            "Do not repeat the previous failed implementation without addressing the reported failure.",
            retryExecutor.LastRequest!.Prompt);
        Assert.Contains("compile error: missing semicolon", retryExecutor.LastRequest!.Prompt);
    }

    [Fact]
    public async Task ExecuteSpecificWorkItemAsync_Throws_WhenWorkItemDoesNotExist()
    {
        await using var db = new FactoryDbContext(_options);
        var executor = new FakeAgentExecutor(_ => throw new InvalidOperationException("should not be called"));
        var orchestrator = MakeOrchestrator(db, executor);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            orchestrator.ExecuteSpecificWorkItemAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_PersistsAgentRun_BeforeExecutionReturns()
    {
        var workItemId = await SeedWorkItemAsync();
        var attemptSeenDuringExecution = -1;

        var executor = new FakeAgentExecutor(_ =>
        {
            // Read through a brand-new DbContext against the same
            // connection, mid-call — proves the AgentRun row was persisted
            // BEFORE the agent even returned, not after.
            using var midExecutionDb = new FactoryDbContext(_options);
            var pending = midExecutionDb.HermesExecutions.SingleOrDefault(x => x.WorkItemId == workItemId);
            attemptSeenDuringExecution = pending?.AttemptNumber ?? -1;

            return new AgentExecutionResult(true, "done", null, 5);
        });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.Equal(1, attemptSeenDuringExecution);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_RecoversAndCompletes_AfterSimulatedRestart()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        // Simulate a crash: the task is stuck Running from a "previous process".
        await using (var crashedDb = new FactoryDbContext(_options))
        {
            await new TaskStateManager(crashedDb).TryStartAsync(workItemId);
        }

        // "Restart": recover orphaned tasks with a fresh DbContext instance.
        await using (var recoveryDb = new FactoryDbContext(_options))
        {
            await new TaskStateManager(recoveryDb).RecoverOrphanedTasksAsync();
        }

        await using (var checkDb = new FactoryDbContext(_options))
        {
            var recoveredItem = await checkDb.WorkItems.SingleAsync(x => x.Id == workItemId);
            Assert.Equal(WorkItemStatus.Repairing.ToString(), recoveredItem.Status);
        }

        // Now the orchestrator (fresh components) should be able to pick it up again.
        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "recovered and done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);

        var finalItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), finalItem.Status);
        Assert.Equal(2, finalItem.AttemptCount);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_TransitionsToRepairing_WhenAgentSucceedsButValidationFails()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "looks done to me", null, 5));

        var failingValidation = new ValidationResult(
            false, true, "build ok",
            false, "1 failed",
            new[] { new ValidationFailure("Test", "error: assertion failed") });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor, validationResult: failingValidation);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);

        var validationRun = await db.ValidationRuns.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.False(validationRun.Success);
        Assert.True(validationRun.BuildSucceeded);
        Assert.False(validationRun.TestsSucceeded);
        Assert.Contains("assertion failed", validationRun.FailuresJson);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_EscalatesToHumanReview_WhenPolicySetAndAttemptsExhausted()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "agent failed", 5));

        var repairOptions = new RepairOptions { Policy = RepairExhaustionPolicy.HumanReview };

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor, repairOptions: repairOptions);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);

        var project = await db.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.HumanReview, project.Status);

        var transition = await db.WorkflowTransitions.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(ProjectStatus.HumanReview, transition.ToStatus);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_DoesNotEscalate_WhenPolicyIsFailed_EvenIfExhausted()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 1);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "agent failed", 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem.Status);

        var project = await db.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.Implementation, project.Status);

        Assert.Empty(db.WorkflowTransitions);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_TransitionsToRepairing_WhenReviewHasCriticalFinding()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "looks done", null, 5));

        var blockingReview = new ReviewResult(
            workItemId, true,
            new[]
            {
                new ReviewFinding(
                    ReviewFindingSeverity.Critical, ReviewCategory.Security, "Auth.cs", 10,
                    "hardcoded secret", "use a secret manager")
            });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor, reviewResult: blockingReview);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Contains("hardcoded secret", outcome.Error);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);

        var reviewRun = await db.ReviewRuns.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.True(reviewRun.BlocksCompletion);
        Assert.Contains("hardcoded secret", reviewRun.FindingsJson);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_Completes_WhenOnlyLowFindingsExist()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "looks done", null, 5));

        var nonBlockingReview = new ReviewResult(
            workItemId, false,
            new[]
            {
                new ReviewFinding(
                    ReviewFindingSeverity.Low, ReviewCategory.Maintainability, null, null,
                    "minor style nit", "consider renaming")
            });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor, reviewResult: nonBlockingReview);

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem.Status);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_Completes_WhenMediumFindingExists_AndPolicyDoesNotBlock()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "looks done", null, 5));

        var mediumReview = new ReviewResult(
            workItemId, false,
            new[]
            {
                new ReviewFinding(
                    ReviewFindingSeverity.Medium, ReviewCategory.TestCoverage, null, null,
                    "missing edge case test", "add a test for empty input")
            });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(
            db, executor, reviewResult: mediumReview, reviewOptions: new ReviewOptions { BlockOnMediumFindings = false });

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.True(outcome!.Success);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem.Status);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_TransitionsToRepairing_WhenMediumFindingExists_AndPolicyBlocks()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "looks done", null, 5));

        var mediumReview = new ReviewResult(
            workItemId, false,
            new[]
            {
                new ReviewFinding(
                    ReviewFindingSeverity.Medium, ReviewCategory.TestCoverage, null, null,
                    "missing edge case test", "add a test for empty input")
            });

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(
            db, executor, reviewResult: mediumReview, reviewOptions: new ReviewOptions { BlockOnMediumFindings = true });

        var outcome = await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.False(outcome!.Success);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem.Status);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEventType> RecordedEventTypes { get; } = new();

        public Task LogAsync(
            Guid executionId,
            AuditEventType eventType,
            AuditSeverity severity,
            string message,
            Guid? projectId = null,
            Guid? workItemId = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            Guid? correlationId = null,
            Guid? operationId = null,
            Guid? runId = null,
            string? outcome = null,
            string? actorType = null,
            string? actorId = null,
            long? durationMs = null,
            CancellationToken cancellationToken = default)
        {
            RecordedEventTypes.Add(eventType);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_RecordsAuditTrail_ForFullSuccessfulRun()
    {
        var workItemId = await SeedWorkItemAsync();
        var auditLogger = new RecordingAuditLogger();

        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor, auditLogger: auditLogger);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        Assert.Contains(AuditEventType.WorkflowStarted, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.ModelSelected, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.AgentStarted, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.AgentCompleted, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.BuildResult, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.ValidationResult, auditLogger.RecordedEventTypes);
        Assert.Contains(AuditEventType.WorkflowCompleted, auditLogger.RecordedEventTypes);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_RecordsHeartbeatAndCheckpoint_OnSuccess()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.NotNull(workItem.LastHeartbeatAtUtc);
        Assert.Equal("ReviewPassed", workItem.LastCheckpoint);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_FeedsRepairContext_IntoRetryPrompt()
    {
        var workItemId = await SeedWorkItemAsync(maxAttempts: 3);

        var failingExecutor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "compile error: missing semicolon", 5));

        await using (var db1 = new FactoryDbContext(_options))
        {
            var orchestrator1 = MakeOrchestrator(db1, failingExecutor);
            await orchestrator1.ExecuteNextReadyTaskAsync(_projectId);
        }

        var retryExecutor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "fixed it", null, 5));

        await using var db2 = new FactoryDbContext(_options);
        var orchestrator2 = MakeOrchestrator(db2, retryExecutor);
        var outcome = await orchestrator2.ExecuteNextReadyTaskAsync(_projectId);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.NotNull(retryExecutor.LastRequest);
        Assert.Contains(
            "Do not repeat the previous failed implementation without addressing the reported failure.",
            retryExecutor.LastRequest!.Prompt);
        Assert.Contains("compile error: missing semicolon", retryExecutor.LastRequest!.Prompt);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_PersistsEvaluationRun_OnSuccess()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var evaluationRun = await db.EvaluationRuns.SingleAsync(x => x.WorkItemId == workItemId);
        Assert.True(evaluationRun.BuildSucceeded);
        Assert.True(evaluationRun.IsFullyValidated);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_RecordsVerifiedLesson_AfterFullyValidatedEvaluation()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var lesson = await db.Lessons.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(KnowledgeLevel.Verified, lesson.Level);
        Assert.NotEmpty(lesson.Evidence);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_RequestsFinalApproval_WhenAllWorkItemsCompleted()
    {
        var workItemId = await SeedWorkItemAsync();

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var project = await db.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.HumanReview, project.Status);

        var approval = await db.ApprovalRequests.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(ApprovalDecision.PendingApproval, approval.Decision);
    }

    [Fact]
    public async Task ExecuteNextReadyTaskAsync_DoesNotRequestFinalApproval_WhileOtherWorkItemsStillIncomplete()
    {
        var workItemId = await SeedWorkItemAsync();

        await using (var seedDb = new FactoryDbContext(_options))
        {
            seedDb.WorkItems.Add(new WorkItem
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Title = "Second task",
                Description = "Still pending.",
                Type = WorkItemType.Implementation,
                Status = WorkItemStatus.Blocked.ToString(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "done", null, 5));

        await using var db = new FactoryDbContext(_options);
        var orchestrator = MakeOrchestrator(db, executor);

        await orchestrator.ExecuteNextReadyTaskAsync(_projectId);

        var project = await db.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.Implementation, project.Status);
        Assert.Empty(db.ApprovalRequests);
    }
}
