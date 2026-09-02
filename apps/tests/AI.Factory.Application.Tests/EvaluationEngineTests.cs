using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class EvaluationEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public EvaluationEngineTests()
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
            Name = "Evaluation Test",
            Slug = "evaluation-test",
            RootPath = "unused",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private EvaluationEngine MakeEngine(FactoryDbContext db)
    {
        return new EvaluationEngine(
            new ValidationRunRepository(db),
            new ReviewRunRepository(db),
            new ApprovalRepository(db));
    }

    private async Task<WorkItem> SeedWorkItemAsync(FactoryDbContext db, int attemptCount = 1)
    {
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Title = "Task",
            Status = WorkItemStatus.Completed.ToString(),
            AttemptCount = attemptCount,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        db.WorkItems.Add(workItem);
        await db.SaveChangesAsync();
        return workItem;
    }

    private static async Task AddValidationRunAsync(
        FactoryDbContext db, Guid workItemId, bool buildOk, bool? testsOk, DateTime createdAtUtc)
    {
        db.ValidationRuns.Add(new ValidationRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            Success = buildOk && testsOk == true,
            BuildSucceeded = buildOk,
            BuildOutput = "build output",
            TestsSucceeded = testsOk,
            TestOutput = "test output",
            CreatedAtUtc = createdAtUtc
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EvaluateAsync_IsFullyValidated_WhenAllCleanAndNoApprovalInvolved()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        db.ReviewRuns.Add(new ReviewRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            BlocksCompletion = false,
            FindingsJson = "[]",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.True(result.BuildSucceeded);
        Assert.True(result.TestsSucceeded);
        Assert.False(result.ReviewBlocked);
        Assert.Null(result.HumanApproval);
        Assert.Equal(RegressionStatus.Unknown, result.Regression);
        Assert.True(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_IsNotFullyValidated_WhenBuildFailed()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        await AddValidationRunAsync(db, workItem.Id, false, null, DateTime.UtcNow);

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.False(result.BuildSucceeded);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_IsNotFullyValidated_WhenReviewBlocks()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        db.ReviewRuns.Add(new ReviewRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            BlocksCompletion = true,
            FindingsJson = "[{\"severity\":\"Critical\"}]",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.True(result.ReviewBlocked);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_IsNotFullyValidated_WhenLatestApprovalIsRejected()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        db.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Workflow = ProjectStatus.Implementation,
            Reason = "repair exhausted",
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = "system",
            Decision = ApprovalDecision.Rejected,
            DecidedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.Equal(ApprovalDecision.Rejected, result.HumanApproval);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_IsFullyValidated_WhenLatestApprovalIsApproved()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        db.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Workflow = ProjectStatus.Implementation,
            Reason = "repair exhausted",
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = "system",
            Decision = ApprovalDecision.Approved,
            DecidedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.Equal(ApprovalDecision.Approved, result.HumanApproval);
        Assert.True(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_DetectsRegression_WhenLatestRunFailsAfterPreviousSucceeded()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db, attemptCount: 2);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow.AddMinutes(-5));
        await AddValidationRunAsync(db, workItem.Id, true, false, DateTime.UtcNow);

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.Equal(RegressionStatus.Regressed, result.Regression);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public async Task EvaluateAsync_DetectsImprovement_WhenLatestRunSucceedsAfterPreviousFailed()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db, attemptCount: 2);

        await AddValidationRunAsync(db, workItem.Id, true, false, DateTime.UtcNow.AddMinutes(-5));
        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.Equal(RegressionStatus.Improved, result.Regression);
    }

    [Fact]
    public async Task EvaluateAsync_DerivesRepairCount_FromAttemptCountMinusOne()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db, attemptCount: 3);

        await AddValidationRunAsync(db, workItem.Id, true, true, DateTime.UtcNow);

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.Equal(2, result.RepairCount);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsFalseBuildAndUnknownRegression_WhenNoValidationRunsExist()
    {
        await using var db = new FactoryDbContext(_options);
        var workItem = await SeedWorkItemAsync(db);

        var result = await MakeEngine(db).EvaluateAsync(workItem);

        Assert.False(result.BuildSucceeded);
        Assert.Null(result.TestsSucceeded);
        Assert.Equal(RegressionStatus.Unknown, result.Regression);
        Assert.False(result.IsFullyValidated);
    }
}
