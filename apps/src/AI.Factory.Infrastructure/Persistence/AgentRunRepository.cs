using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AgentRunRepository : IAgentRunRepository
{
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
}
