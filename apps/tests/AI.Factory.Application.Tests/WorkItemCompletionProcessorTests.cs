using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Learning;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.13 — WorkItemCompletionProcessor is the post-verification stage
/// (independent review, then evaluation/learning on a review pass) that
/// AgentRunWorkItemSync delegates into after IVerificationGate reports
/// Passed. It never re-decides build/test outcome and its only output is
/// the next WorkItemStatus for the caller to apply.
/// </summary>
public sealed class WorkItemCompletionProcessorTests : IDisposable
{
    private sealed class FakeCodeReviewer : ICodeReviewer
    {
        private readonly ReviewResult _result;

        public FakeCodeReviewer(ReviewResult result) => _result = result;

        public Task<ReviewResult> ReviewAsync(ReviewContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class ThrowingEvaluationEngine : IEvaluationEngine
    {
        public Task<EvaluationResult> EvaluateAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Evaluation storage is unavailable.");
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public WorkItemCompletionProcessorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Completion Processor Test",
            Slug = "completion-processor-test",
            RootPath = "unused",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<(Project Project, WorkItem WorkItem, AgentRun AgentRun)> SeedAsync(FactoryDbContext db)
    {
        var project = await db.Projects.SingleAsync(x => x.Id == _projectId);

        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Title = "Implement feature",
            Description = "Do the thing.",
            Type = WorkItemType.Implementation,
            Status = WorkItemStatus.Testing.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db.WorkItems.Add(workItem);
        await db.SaveChangesAsync();

        var agentRun = new AgentRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            ProjectId = _projectId,
            Status = AgentRunStatus.Succeeded,
            Output = "implemented the feature"
        };

        return (project, workItem, agentRun);
    }

    private static VerificationResult PassedResult() =>
        new(VerificationStatus.Passed, "build ok; tests ok", 0, TimeSpan.FromSeconds(1));

    private WorkItemCompletionProcessor MakeProcessor(
        FactoryDbContext db,
        ReviewResult reviewResult,
        ReviewOptions? reviewOptions = null,
        IEvaluationEngine? evaluationEngine = null)
    {
        var validationRunRepository = new ValidationRunRepository(db);
        var reviewRunRepository = new ReviewRunRepository(db);
        var artifactRepository = new ArtifactRepository(db);
        var contextProvider = new LocalContextProvider(db);
        var reviewContextBuilder = new ReviewContextBuilder(contextProvider, artifactRepository, validationRunRepository);
        var evaluationRunRepository = new EvaluationRunRepository(db);
        var learningService = new LearningService(new LearningStore(db));

        return new WorkItemCompletionProcessor(
            validationRunRepository,
            reviewContextBuilder,
            new FakeCodeReviewer(reviewResult),
            reviewRunRepository,
            reviewOptions ?? new ReviewOptions(),
            evaluationEngine ?? new EvaluationEngine(validationRunRepository, reviewRunRepository, new ApprovalRepository(db)),
            evaluationRunRepository,
            learningService,
            NullLogger<WorkItemCompletionProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_ReviewPasses_ReturnsCompleted_AndPersistsValidationReviewEvaluationRuns_AndRecordsLesson()
    {
        await using var db = new FactoryDbContext(_options);
        var (project, workItem, agentRun) = await SeedAsync(db);

        var reviewResult = new ReviewResult(workItem.Id, false, Array.Empty<ReviewFinding>());
        var processor = MakeProcessor(db, reviewResult);

        var decision = await processor.ProcessAsync(project, workItem, agentRun, PassedResult());

        Assert.Equal(WorkItemStatus.Completed, decision.Target);
        Assert.Null(decision.ErrorMessage);

        Assert.NotNull(await db.ValidationRuns.SingleOrDefaultAsync(x => x.WorkItemId == workItem.Id));
        var reviewRun = await db.ReviewRuns.SingleAsync(x => x.WorkItemId == workItem.Id);
        Assert.False(reviewRun.BlocksCompletion);
        var evaluationRun = await db.EvaluationRuns.SingleAsync(x => x.WorkItemId == workItem.Id);
        Assert.True(evaluationRun.IsFullyValidated);

        var lesson = await db.Lessons.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(KnowledgeLevel.Verified, lesson.Level);
        Assert.Equal("implemented the feature", lesson.Solution);
    }

    [Fact]
    public async Task ProcessAsync_ReviewBlocks_ReturnsRepairing_WithBoundedFindingsSummary_AndNoEvaluationOrLesson()
    {
        await using var db = new FactoryDbContext(_options);
        var (project, workItem, agentRun) = await SeedAsync(db);

        var findings = new[]
        {
            new ReviewFinding(
                ReviewFindingSeverity.Critical, ReviewCategory.Security, "Auth.cs", 10,
                "hardcoded secret", "use a secret manager")
        };
        var reviewResult = new ReviewResult(workItem.Id, true, findings);
        var processor = MakeProcessor(db, reviewResult);

        var decision = await processor.ProcessAsync(project, workItem, agentRun, PassedResult());

        Assert.Equal(WorkItemStatus.Repairing, decision.Target);
        Assert.Contains("hardcoded secret", decision.ErrorMessage);

        Assert.Empty(db.EvaluationRuns.Where(x => x.WorkItemId == workItem.Id));
        Assert.Empty(db.Lessons.Where(x => x.ProjectId == _projectId));
    }

    [Fact]
    public async Task ProcessAsync_MediumFindingBlocksPerReviewOptions_ReturnsRepairing()
    {
        await using var db = new FactoryDbContext(_options);
        var (project, workItem, agentRun) = await SeedAsync(db);

        var findings = new[]
        {
            new ReviewFinding(
                ReviewFindingSeverity.Medium, ReviewCategory.TestCoverage, null, null,
                "missing edge case test", "add a test for empty input")
        };
        var reviewResult = new ReviewResult(workItem.Id, false, findings);
        var processor = MakeProcessor(db, reviewResult, reviewOptions: new ReviewOptions { BlockOnMediumFindings = true });

        var decision = await processor.ProcessAsync(project, workItem, agentRun, PassedResult());

        Assert.Equal(WorkItemStatus.Repairing, decision.Target);
        Assert.Contains("missing edge case test", decision.ErrorMessage);
    }

    [Fact]
    public async Task ProcessAsync_EvaluationThrows_StillReturnsCompleted_ExceptionSwallowedAndLogged()
    {
        await using var db = new FactoryDbContext(_options);
        var (project, workItem, agentRun) = await SeedAsync(db);

        var reviewResult = new ReviewResult(workItem.Id, false, Array.Empty<ReviewFinding>());
        var processor = MakeProcessor(db, reviewResult, evaluationEngine: new ThrowingEvaluationEngine());

        var decision = await processor.ProcessAsync(project, workItem, agentRun, PassedResult());

        Assert.Equal(WorkItemStatus.Completed, decision.Target);
        Assert.Null(decision.ErrorMessage);
        Assert.Empty(db.EvaluationRuns.Where(x => x.WorkItemId == workItem.Id));
    }
}
