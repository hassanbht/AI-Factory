using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IAuditLogRepository
{
    Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    Task<AuditLogPage> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deletes every entry older than cutoffUtc — the retention-configuration enforcement point. Returns the number of rows deleted.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>Every entry sharing one CorrelationId, oldest first — the full timeline of one operation.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByCorrelationIdAsync(
        Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Most recent entries across all projects, newest first, bounded by count.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>Most recent Warning/Error/Critical entries, newest first, bounded by count — optionally scoped to one project.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(
        int count, Guid? projectId = null, CancellationToken cancellationToken = default);
}

public sealed record AuditLogQuery(
    Guid? ProjectId = null,
    Guid? ExecutionId = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AuditLogPage(
    IReadOnlyList<AuditLogEntry> Items,
    int TotalCount,
    int Page,
    int PageSize);
