using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly FactoryDbContext _db;

    public AuditLogRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _db.AuditLogEntries.AddAsync(entry, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuditLogPage> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        var filtered = _db.AuditLogEntries.AsQueryable();

        if (query.ProjectId is not null)
        {
            filtered = filtered.Where(x => x.ProjectId == query.ProjectId);
        }

        if (query.ExecutionId is not null)
        {
            filtered = filtered.Where(x => x.ExecutionId == query.ExecutionId);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        var items = await filtered
            .OrderByDescending(x => x.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AuditLogPage(items, totalCount, page, pageSize);
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var expired = await _db.AuditLogEntries
            .Where(x => x.TimestampUtc < cutoffUtc)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        _db.AuditLogEntries.RemoveRange(expired);
        await _db.SaveChangesAsync(cancellationToken);

        return expired.Count;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetByCorrelationIdAsync(
        Guid correlationId, CancellationToken cancellationToken = default)
    {
        return await _db.AuditLogEntries
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(
        int count, CancellationToken cancellationToken = default)
    {
        return await _db.AuditLogEntries
            .OrderByDescending(x => x.TimestampUtc)
            .Take(Math.Clamp(count, 1, 500))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(
        int count, Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        // Severity is stored via HasConversion<string>() — a >= comparison
        // would translate to lexicographic string ordering in SQL, not enum
        // ordinal ("Critical" < "Error" < "Info" < "Warning" alphabetically).
        // != Info is correct for this 4-value enum either way.
        var query = _db.AuditLogEntries.Where(x => x.Severity != AuditSeverity.Info);

        if (projectId is not null)
        {
            query = query.Where(x => x.ProjectId == projectId);
        }

        return await query
            .OrderByDescending(x => x.TimestampUtc)
            .Take(Math.Clamp(count, 1, 500))
            .ToListAsync(cancellationToken);
    }
}
