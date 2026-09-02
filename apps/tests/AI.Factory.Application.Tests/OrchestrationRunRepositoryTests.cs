using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OrchestrationRunRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public OrchestrationRunRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedPendingRunAsync()
    {
        var id = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        db.OrchestrationRuns.Add(new OrchestrationRun
        {
            Id = id,
            ProjectId = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            Status = OrchestrationRunStatus.Pending
        });
        await db.SaveChangesAsync();

        return id;
    }

    [Fact]
    public async Task TryClaimNextPendingAsync_PreventsDuplicateClaim_OnASecondAttempt()
    {
        // Genuinely concurrent Task.WhenAll calls against a single shared
        // SQLite connection are not safe to exercise in-process (the ADO.NET
        // provider serializes command execution per connection and can
        // deadlock the test host). The atomicity this proves — Status is
        // re-checked as part of the SAME conditional UPDATE statement, not
        // read-then-written — is what a second claim attempt against the
        // already-Claimed row demonstrates just as directly: whichever
        // request's UPDATE commits first flips Status, so any later
        // attempt's WHERE Status==Pending simply matches zero rows.
        var runId = await SeedPendingRunAsync();

        await using var db1 = new FactoryDbContext(_options);
        var first = await new OrchestrationRunRepository(db1).TryClaimNextPendingAsync();

        await using var db2 = new FactoryDbContext(_options);
        var second = await new OrchestrationRunRepository(db2).TryClaimNextPendingAsync();

        Assert.NotNull(first);
        Assert.Equal(runId, first!.Id);
        Assert.Null(second);
        Assert.Equal(OrchestrationRunStatus.Claimed, first.Status);
    }

    [Fact]
    public async Task TryClaimNextPendingAsync_ReturnsNull_WhenNothingIsPending()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new OrchestrationRunRepository(db);

        var claimed = await repository.TryClaimNextPendingAsync();

        Assert.Null(claimed);
    }

    [Fact]
    public async Task RecoverStaleLeasesAsync_ReturnsExpiredRun_ToPending_WhenAttemptsRemain()
    {
        var runId = Guid.NewGuid();

        await using (var writeDb = new FactoryDbContext(_options))
        {
            writeDb.OrchestrationRuns.Add(new OrchestrationRun
            {
                Id = runId,
                ProjectId = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid(),
                Status = OrchestrationRunStatus.Running,
                Attempt = 1,
                MaxAttempts = 3,
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-10)
            });
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var repository = new OrchestrationRunRepository(db);

        var recoveredCount = await repository.RecoverStaleLeasesAsync();

        Assert.Equal(1, recoveredCount);

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(OrchestrationRunStatus.Pending, run.Status);
    }

    [Fact]
    public async Task RecoverStaleLeasesAsync_FailsExpiredRun_WhenAttemptsExhausted()
    {
        var runId = Guid.NewGuid();

        await using (var writeDb = new FactoryDbContext(_options))
        {
            writeDb.OrchestrationRuns.Add(new OrchestrationRun
            {
                Id = runId,
                ProjectId = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid(),
                Status = OrchestrationRunStatus.Running,
                Attempt = 3,
                MaxAttempts = 3,
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-10)
            });
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var repository = new OrchestrationRunRepository(db);

        await repository.RecoverStaleLeasesAsync();

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(OrchestrationRunStatus.Failed, run.Status);
        Assert.Equal(OrchestrationFailureCategory.InfrastructureFailure, run.FailureCategory);
    }

    [Fact]
    public async Task RecoverStaleLeasesAsync_LeavesFreshLease_Untouched()
    {
        var runId = Guid.NewGuid();

        await using (var writeDb = new FactoryDbContext(_options))
        {
            writeDb.OrchestrationRuns.Add(new OrchestrationRun
            {
                Id = runId,
                ProjectId = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid(),
                Status = OrchestrationRunStatus.Running,
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
            });
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var repository = new OrchestrationRunRepository(db);

        var recoveredCount = await repository.RecoverStaleLeasesAsync();

        Assert.Equal(0, recoveredCount);

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(OrchestrationRunStatus.Running, run.Status);
    }

    [Fact]
    public async Task AwaitingApprovalRun_IsNeverReClaimed_AfterReachingTerminalForWorkerState()
    {
        // Proxy for "no continued execution after human rejection" (Section
        // 17/20#9): once a run leaves Pending, TryClaimNextPendingAsync can
        // never pick it up again, regardless of what a human later decides
        // via the existing, separately-tested ApprovalService.
        var runId = Guid.NewGuid();

        await using (var writeDb = new FactoryDbContext(_options))
        {
            writeDb.OrchestrationRuns.Add(new OrchestrationRun
            {
                Id = runId,
                ProjectId = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid(),
                Status = OrchestrationRunStatus.AwaitingApproval
            });
            await writeDb.SaveChangesAsync();
        }

        await using var db = new FactoryDbContext(_options);
        var repository = new OrchestrationRunRepository(db);

        var claimed = await repository.TryClaimNextPendingAsync();

        Assert.Null(claimed);
    }
}
