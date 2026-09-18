using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AgentRunRepository : IAgentRunRepository
{
    // Duplicated from AgentRunTransitions.TerminalStatuses rather than
    // calling it: EF Core cannot translate a call to an external static
    // method inside a Where/AnyAsync predicate into SQL. The same tradeoff
    // is already made in this codebase by
    // TaskSchedulerHostedService.TerminalProjectStatuses.
    private static readonly AgentRunStatus[] TerminalAgentRunStatuses =
    [
        AgentRunStatus.Succeeded, AgentRunStatus.Failed, AgentRunStatus.TimedOut, AgentRunStatus.Cancelled
    ];

    private readonly FactoryDbContext _db;

    public AgentRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<AgentRun> CreateAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        await _db.AgentRuns.AddAsync(agentRun, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return agentRun;
        }
        catch (DbUpdateException)
        {
            _db.Entry(agentRun).State = EntityState.Detached;

            var existing = await GetByCorrelationIdAsync(agentRun.CorrelationId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task<AgentRun?> GetByIdAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentRuns
            .Include(x => x.AgentDefinition)
            .FirstOrDefaultAsync(x => x.Id == agentRunId, cancellationToken);
    }

    public async Task<AgentRun?> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentRuns
            .FirstOrDefaultAsync(x => x.CorrelationId == correlationId, cancellationToken);
    }

    public async Task UpdateAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.AgentRuns
            .AnyAsync(x => x.Id == agentRun.Id, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException($"AgentRun '{agentRun.Id}' was not found.");
        }

        _db.AgentRuns.Update(agentRun);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AgentRun?> TryClaimNextPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var candidate = await _db.AgentRuns
            .Include(x => x.AgentDefinition)
            .Where(x => x.Status == AgentRunStatus.Queued)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        candidate.TransitionTo(AgentRunStatus.Running);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return candidate;
        }
        catch (ConcurrencyConflictException)
        {
            // Another caller claimed this row first — not an error, just a
            // lost race. The next poll will pick up whatever is next.
            return null;
        }
    }

    public async Task HeartbeatAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == agentRunId, cancellationToken);
        if (run is null || AgentRunTransitions.IsTerminal(run.Status))
        {
            return;
        }

        run.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Transient collision on optimistic concurrency token — safe to ignore on heartbeat
        }
    }

    public async Task<int> RecoverOrphanedRunsAsync(
        DateTime? startedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        var query = _db.AgentRuns.Where(x => x.Status == AgentRunStatus.Running);

        if (startedBeforeUtc is { } cutoffUtc)
        {
            // Multi-worker safe: only a run whose proof of life (both UpdatedAtUtc heartbeat
            // and StartedAtUtc/CreatedAtUtc) predates the cutoff. A genuinely executing run
            // being heartbeated by another worker is left completely untouched.
            query = query.Where(x => x.UpdatedAtUtc < cutoffUtc && (x.StartedAtUtc ?? x.CreatedAtUtc) < cutoffUtc);
        }

        var orphaned = await query.ToListAsync(cancellationToken);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        var reason = startedBeforeUtc is null
            ? "the Worker process restarted while this run was executing"
            : "no progress before the configured stale-execution timeout";

        foreach (var run in orphaned)
        {
            // Running -> Failed is an allowed edge in AgentRunTransitions, so
            // this goes through the same validated transition every other
            // writer uses rather than assigning Status directly.
            run.TransitionTo(AgentRunStatus.Failed);
            run.ErrorMessage = $"{AgentRunRecovery.OrphanedMarker}: {reason}.";
            run.FailureCategory = FailureCategory.Infrastructure;
            run.CompletedAtUtc = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return orphaned.Count;
        }
        catch (ConcurrencyConflictException)
        {
            // Race with another recovering worker: exactly one worker wins the Version update.
            return 0;
        }
    }

    public async Task<AgentRunPage> GetPagedAsync(
        AgentRunQuery query,
        CancellationToken cancellationToken = default)
    {
        var filtered = _db.AgentRuns.AsQueryable();

        if (query.ProjectId is { } projectId)
        {
            filtered = filtered.Where(x => x.ProjectId == projectId);
        }

        if (query.AgentDefinitionId is { } agentDefinitionId)
        {
            filtered = filtered.Where(x => x.AgentDefinitionId == agentDefinitionId);
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(x => x.Status == status);
        }

        if (query.FromUtc is { } fromUtc)
        {
            filtered = filtered.Where(x => x.CreatedAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            filtered = filtered.Where(x => x.CreatedAtUtc <= toUtc);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        var items = await filtered
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new AgentRunPage(items, totalCount, query.Page, query.PageSize);
    }

    public async Task<int> CountByStatusAsync(
        AgentRunStatus status,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentRuns.CountAsync(x => x.Status == status, cancellationToken);
    }

    public async Task<bool> HasActiveRunsForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await _db.AgentRuns.AnyAsync(
            x => x.ProjectId == projectId && !TerminalAgentRunStatuses.Contains(x.Status),
            cancellationToken);
    }
}
