using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Lightweight read model over existing entities plus the audit trail —
/// deliberately not an analytics engine, just enough to answer "what is
/// this project's/operation's current state" for an operator.
/// </summary>
public interface IOperationalDiagnosticsService
{
    Task<ProjectDiagnosticSummary?> GetProjectSummaryAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<CorrelationSummary?> GetCorrelationSummaryAsync(Guid correlationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(
        int count, Guid? projectId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogEntry>> GetRecentOperationsAsync(int count, CancellationToken cancellationToken = default);
}

public sealed record ProjectDiagnosticSummary(
    Guid ProjectId,
    string ProjectStatus,
    Guid? LatestTaskId,
    string? LatestTaskStatus,
    int RepairAttemptCount,
    Guid? LatestRunId,
    string? LatestRunStatus,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    FailureCategory? LastFailureCategory,
    string? LastFailureMessage,
    ApprovalDecision? LatestApprovalDecision,
    IReadOnlyDictionary<string, int>? WorkItemStatusCounts = null,
    int ActiveAgentRuns = 0,
    int StaleAgentRuns = 0,
    long? ExecutionDurationMs = null,
    int AttemptCount = 0);

public sealed record CorrelationSummary(
    Guid CorrelationId,
    int EntryCount,
    DateTime FirstEntryAtUtc,
    DateTime LastEntryAtUtc,
    bool HasFailures,
    IReadOnlyList<AuditLogEntry> Entries);
