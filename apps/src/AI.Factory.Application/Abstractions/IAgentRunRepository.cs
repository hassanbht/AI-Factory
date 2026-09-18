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

    /// <summary>
    /// Phase 18.10 §16 — whether any AgentRun linked to this Project is still
    /// in a non-terminal status (Created/Queued/Running/Waiting). Used by
    /// IProjectCompletionGate to avoid advancing a Project while work is
    /// still in flight, even if every WorkItem already looks Completed.
    /// </summary>
    Task<bool> HasActiveRunsForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 18.26 §5/§7 — recovers AgentRuns left in <c>Running</c> by a
    /// Worker that died mid-execution. Until this existed, such a run stayed
    /// Running forever, and because <see cref="HasActiveRunsForProjectAsync"/>
    /// gates the Project completion gate, the owning Project could never reach
    /// Testing/HumanReview again: a single Worker crash deadlocked it
    /// permanently.
    ///
    /// <paramref name="startedBeforeUtc"/> null means "every Running run" —
    /// correct only at process startup, where a full-process restart proves
    /// nothing this process owns is genuinely executing. A value restricts the
    /// sweep to runs that started before the cutoff, which is what makes it
    /// safe to run periodically inside a live process. This mirrors
    /// ITaskStateManager's existing two-mode WorkItem recovery exactly.
    ///
    /// Recovery is terminal, never a silent re-execution: the run becomes
    /// Failed with <see cref="Domain.Enums.FailureCategory.Infrastructure"/>
    /// and an <see cref="AgentRunRecovery.OrphanedMarker"/>-prefixed reason, so
    /// deciding what (if anything) runs next stays with the existing
    /// WorkItem-repair and Project-lifecycle owners rather than being
    /// duplicated here.
    /// </summary>
    Task<int> RecoverOrphanedRunsAsync(
        DateTime? startedBeforeUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 18.27 — periodic heartbeat for an active AgentRun. Updates
    /// UpdatedAtUtc under optimistic concurrency so that multi-worker recovery
    /// can distinguish healthy, executing runs from dead worker orphans.
    /// </summary>
    Task HeartbeatAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Phase 18.26 §5 — the one place the "this AgentRun died with its Worker"
/// signal is spelled, so the lifecycle owner can tell a crash apart from a
/// genuine agent/provider failure without re-deriving it from prose.
/// </summary>
public static class AgentRunRecovery
{
    public const string OrphanedMarker = "AgentRunOrphanedByWorkerRestart";

    /// <summary>True when this run was terminated by orphan recovery rather than by its own execution.</summary>
    public static bool WasOrphaned(AgentRun run) =>
        run.ErrorMessage?.StartsWith(OrphanedMarker, StringComparison.Ordinal) == true;
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
