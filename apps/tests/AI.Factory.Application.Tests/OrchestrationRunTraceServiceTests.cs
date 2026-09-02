using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OrchestrationRunTraceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public OrchestrationRunTraceServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<(Guid RunId, Guid WorkItemId)> SeedActiveRunAsync()
    {
        var runId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);

        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            Id = runId,
            ProjectId = Guid.NewGuid(),
            WorkItemId = workItemId,
            Status = OrchestrationRunStatus.Executing
        });

        await db.SaveChangesAsync();

        return (runId, workItemId);
    }

    private OrchestrationRunTraceService MakeService(FactoryDbContext db)
        => new(new OrchestrationRunRepository(db), NullLogger<OrchestrationRunTraceService>.Instance);

    [Fact]
    public async Task StartStepAsync_CreatesRunningStep_ForTheActiveRun()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.AgentExecution, attemptNumber: 1);

        var step = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runId);
        Assert.Equal(OrchestrationStepStatus.Running, step.Status);
        Assert.Equal(OrchestrationStepKind.AgentExecution, step.Stage);
        Assert.Equal(1, step.AttemptNumber);
        Assert.Null(step.CompletedAtUtc);
    }

    [Fact]
    public async Task CompleteStepAsync_MarksTheOpenStepSucceeded()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.Build, attemptNumber: 1);
        await service.CompleteStepAsync(
            workItemId, OrchestrationStepKind.Build, attemptNumber: 1,
            metadata: new Dictionary<string, string> { ["outcome"] = "ok" });

        var step = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runId);
        Assert.Equal(OrchestrationStepStatus.Succeeded, step.Status);
        Assert.NotNull(step.CompletedAtUtc);
        Assert.Contains("outcome", step.MetadataJson);
    }

    [Fact]
    public async Task FailStepAsync_MarksTheOpenStepFailed_WithFailureTypeAndMessage()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.Test, attemptNumber: 2);
        await service.FailStepAsync(workItemId, OrchestrationStepKind.Test, "TestFailure", "3 tests failed.", attemptNumber: 2);

        var step = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runId);
        Assert.Equal(OrchestrationStepStatus.Failed, step.Status);
        Assert.Equal("TestFailure", step.FailureType);
        Assert.Equal("3 tests failed.", step.FailureMessage);
        Assert.NotNull(step.CompletedAtUtc);
    }

    [Fact]
    public async Task SkipStepAsync_CreatesASkippedStep_WithoutAnyPriorStart()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.SkipStepAsync(workItemId, OrchestrationStepKind.Test, "No tests configured.", attemptNumber: 1);

        var step = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runId);
        Assert.Equal(OrchestrationStepStatus.Skipped, step.Status);
        Assert.Equal("No tests configured.", step.FailureMessage);
        Assert.Equal(step.StartedAtUtc, step.CompletedAtUtc);
    }

    [Fact]
    public async Task Steps_AreOrderedBySequence_AsAssignedAtPersistenceTime()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.WorkspacePreparation);
        await service.CompleteStepAsync(workItemId, OrchestrationStepKind.WorkspacePreparation);
        await service.StartStepAsync(workItemId, OrchestrationStepKind.TaskPlanning);
        await service.CompleteStepAsync(workItemId, OrchestrationStepKind.TaskPlanning);
        await service.StartStepAsync(workItemId, OrchestrationStepKind.ContextRetrieval);

        var steps = await db.OrchestrationRunSteps
            .Where(s => s.OrchestrationRunId == runId)
            .OrderBy(s => s.Sequence)
            .ToListAsync();

        Assert.Equal(3, steps.Count);
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(s => s.Sequence));
        Assert.Equal(
            new[] { OrchestrationStepKind.WorkspacePreparation, OrchestrationStepKind.TaskPlanning, OrchestrationStepKind.ContextRetrieval },
            steps.Select(s => s.Stage));
    }

    [Fact]
    public async Task MultipleAttempts_ProduceSeparateStepRows_ForTheSameStage()
    {
        var (runId, workItemId) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.Build, attemptNumber: 1);
        await service.FailStepAsync(workItemId, OrchestrationStepKind.Build, "BuildFailure", "attempt 1 failed", attemptNumber: 1);

        await service.StartStepAsync(workItemId, OrchestrationStepKind.Build, attemptNumber: 2);
        await service.CompleteStepAsync(workItemId, OrchestrationStepKind.Build, attemptNumber: 2);

        var steps = await db.OrchestrationRunSteps
            .Where(s => s.OrchestrationRunId == runId)
            .OrderBy(s => s.Sequence)
            .ToListAsync();

        Assert.Equal(2, steps.Count);
        Assert.Equal(1, steps[0].AttemptNumber);
        Assert.Equal(OrchestrationStepStatus.Failed, steps[0].Status);
        Assert.Equal(2, steps[1].AttemptNumber);
        Assert.Equal(OrchestrationStepStatus.Succeeded, steps[1].Status);
    }

    [Fact]
    public async Task Steps_AreAttachedOnlyToTheirOwnRun_NotAnUnrelatedRun()
    {
        var (runIdA, workItemIdA) = await SeedActiveRunAsync();
        var (runIdB, workItemIdB) = await SeedActiveRunAsync();

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.StartStepAsync(workItemIdA, OrchestrationStepKind.AgentExecution);
        await service.StartStepAsync(workItemIdB, OrchestrationStepKind.AgentExecution);

        await service.CompleteStepAsync(workItemIdA, OrchestrationStepKind.AgentExecution);

        var stepA = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runIdA);
        var stepB = await db.OrchestrationRunSteps.SingleAsync(s => s.OrchestrationRunId == runIdB);

        Assert.Equal(OrchestrationStepStatus.Succeeded, stepA.Status);
        Assert.Equal(OrchestrationStepStatus.Running, stepB.Status);
    }
}
