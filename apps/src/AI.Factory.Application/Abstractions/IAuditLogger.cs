using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// The write-side facade callers use instead of touching IAuditLogRepository
/// directly — the implementation is responsible for never letting an audit
/// persistence failure propagate into the caller's own workflow (unless
/// explicitly configured as critical), and for never persisting a
/// secret-shaped metadata value.
///
/// CorrelationId/OperationId/RunId are optional — when omitted, the
/// implementation falls back to the ambient IOperationContext (if an
/// IOperationScope is open) and finally to executionId, so every existing
/// call site gets correlation for free without being touched (Phase 17.3).
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
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
        CancellationToken cancellationToken = default);
}

/// <summary>Backward-compatible default for constructors that made IAuditLogger an optional dependency.</summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public static readonly NullAuditLogger Instance = new();

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
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
