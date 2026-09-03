using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class OrchestrationRunRepository : IOrchestrationRunRepository
{
    private static readonly OrchestrationRunStatus[] TerminalStatuses =
    {
        OrchestrationRunStatus.Completed, OrchestrationRunStatus.Failed, OrchestrationRunStatus.Cancelled
    };

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly FactoryDbContext _db;

    public OrchestrationRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default)
    {
        await _db.OrchestrationRuns.AddAsync(run, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public async Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return await _db.OrchestrationRuns
            .Include(x => x.Attempts)
            .Include(x => x.ProgressEvents)
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
    }

    public async Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        return await _db.OrchestrationRuns
            .Where(x => x.WorkItemId == workItemId && !TerminalStatuses.Contains(x.Status))
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
    {
        var candidate = await _db.OrchestrationRuns
            .Where(x => x.Status == OrchestrationRunStatus.Pending)
            .OrderBy(x => x.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        // A single conditional UPDATE — the WHERE re-checks Status==Pending
        // as part of the same atomic statement, so a second Worker racing
        // on the same row gets 0 rows affected, never a double-claim.
        var rowsAffected = await _db.OrchestrationRuns
            .Where(x => x.Id == candidate.Id && x.Status == OrchestrationRunStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, OrchestrationRunStatus.Claimed)
                    .SetProperty(x => x.WorkerHeartbeatAtUtc, now)
                    .SetProperty(x => x.LeaseExpiresAtUtc, now.Add(LeaseDuration)),
                cancellationToken);

        if (rowsAffected == 0)
        {
            return null;
        }

        // Re-querying here would return the SAME tracked instance from this
        // DbContext's identity map with its pre-update (stale) property
        // values — ExecuteUpdateAsync writes the database directly and does
        // not touch the change tracker. Mutate the in-memory object to
        // match what was just written instead of re-fetching it.
        candidate.Status = OrchestrationRunStatus.Claimed;
        candidate.WorkerHeartbeatAtUtc = now;
        candidate.LeaseExpiresAtUtc = now.Add(LeaseDuration);

        return candidate;
    }

    public async Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default)
    {
        if (_db.Entry(run).State == EntityState.Detached)
        {
            _db.OrchestrationRuns.Update(run);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default)
    {
        await _db.OrchestrationRunAttempts.AddAsync(attempt, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default)
    {
        await _db.OrchestrationProgressEvents.AddAsync(progressEvent, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default)
    {
        var maxSequence = await _db.OrchestrationRunSteps
            .Where(x => x.OrchestrationRunId == step.OrchestrationRunId)
            .Select(x => (int?)x.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        step.Sequence = maxSequence + 1;

        await _db.OrchestrationRunSteps.AddAsync(step, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return step;
    }

    public async Task<OrchestrationRunStep?> FindLatestOpenStepAsync(
        Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default)
    {
        return await _db.OrchestrationRunSteps
            .Where(x =>
                x.OrchestrationRunId == orchestrationRunId
                && x.Stage == stage
                && x.AttemptNumber == attemptNumber
                && x.Status == OrchestrationStepStatus.Running)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default)
    {
        if (_db.Entry(step).State == EntityState.Detached)
        {
            _db.OrchestrationRunSteps.Update(step);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        // Load-then-save rather than ExecuteUpdateAsync: this is a
        // low-frequency operation (one cancel request per run, not a
        // high-contention claim), and a caller that already has this run
        // tracked in the same DbContext scope (e.g. OrchestrationService
        // checking Status first) must see the change reflected, which a
        // raw SQL UPDATE bypassing the change tracker would not provide.
        var run = await _db.OrchestrationRuns.FindAsync(new object[] { runId }, cancellationToken);

        if (run is not null)
        {
            run.CancellationRequestedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await _db.OrchestrationRuns
            .Where(x => x.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.WorkerHeartbeatAtUtc, now)
                    .SetProperty(x => x.LeaseExpiresAtUtc, now.Add(leaseDuration)),
                cancellationToken);
    }

    public async Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var stale = await _db.OrchestrationRuns
            .Where(x =>
                (x.Status == OrchestrationRunStatus.Claimed || x.Status == OrchestrationRunStatus.Running)
                && x.LeaseExpiresAtUtc != null
                && x.LeaseExpiresAtUtc < now)
            .ToListAsync(cancellationToken);

        foreach (var run in stale)
        {
            run.ErrorMessage = "Recovered — Worker lease expired (Worker died mid-execution).";
            run.FailureCategory = OrchestrationFailureCategory.InfrastructureFailure;

            run.Status = run.Attempt < run.MaxAttempts
                ? OrchestrationRunStatus.Pending
                : OrchestrationRunStatus.Failed;

            if (run.Status == OrchestrationRunStatus.Failed)
            {
                run.CompletedAtUtc = now;
            }
        }

        if (stale.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }

    public async Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await _db.OrchestrationRuns
            .Where(x => x.Id == runId && (x.Status == OrchestrationRunStatus.Claimed || x.Status == OrchestrationRunStatus.Running))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, OrchestrationRunStatus.Pending)
                    .SetProperty(x => x.WorkerHeartbeatAtUtc, (DateTime?)null)
                    .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(x => x.ErrorMessage, "Execution interrupted by shutdown; queued for resume."),
                cancellationToken);
    }
}
