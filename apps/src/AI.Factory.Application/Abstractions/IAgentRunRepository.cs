using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>Persists the canonical, runtime-agnostic <see cref="AgentRun"/> execution lifecycle record.</summary>
public interface IAgentRunRepository
{
    /// <summary>
    /// Creates a new AgentRun, or — if a row with the same CorrelationId already
    /// exists — returns that existing row instead. This is the idempotency
    /// guarantee: two callers submitting the same logical request concurrently
    /// must never produce two canonical AgentRuns. The database's unique index
    /// on CorrelationId is the final guarantee; this method reconciles the race.
    /// </summary>
    Task<AgentRun> CreateAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default);

    Task<AgentRun?> GetByIdAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default);

    Task<AgentRun?> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the oldest Queued AgentRun by transitioning it to
    /// Running and persisting under its optimistic-concurrency Version token
    /// — the smallest safe claim mechanism for a single Worker instance, no
    /// distributed lock. Returns null if there is nothing to claim, or if
    /// another concurrent caller won the race for the same row. The
    /// returned AgentRun has AgentDefinition already loaded.
    /// </summary>
    Task<AgentRun?> TryClaimNextPendingAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Filtered, paged AgentRun history — bounded page size enforced by the caller (API layer).</summary>
    Task<AgentRunPage> GetPagedAsync(
        AgentRunQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Live count of AgentRuns currently in one status — a derived value, not a monotonic counter, so it belongs here rather than IOperationalMetrics.</summary>
    Task<int> CountByStatusAsync(
        AgentRunStatus status,
        CancellationToken cancellationToken = default);
}

public sealed record AgentRunQuery(
    Guid? ProjectId = null,
    Guid? AgentDefinitionId = null,
    AgentRunStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AgentRunPage(
    IReadOnlyList<AgentRun> Items,
    int TotalCount,
    int Page,
    int PageSize);
