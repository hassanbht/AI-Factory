using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>Persists <see cref="HermesExecution"/> rows — the physical Hermes CLI process execution audit trail for a WorkItem. Renamed from IAgentRunRepository in Phase 18.1.2; see AgentRun for the canonical orchestration-level execution record.</summary>
public interface IHermesExecutionRepository
{
    /// <summary>Persists a row before execution starts (the audit trail must exist before the risky operation).</summary>
    Task CreatePendingAsync(
        HermesExecution execution,
        CancellationToken cancellationToken = default);

    /// <summary>Updates a previously-created row with its outcome.</summary>
    Task CompleteAsync(
        Guid executionId,
        bool success,
        string? output,
        string? error,
        long durationMs,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent execution for a WorkItem, or null if none exists yet — used to feed a previous-attempt summary into a repair prompt.</summary>
    Task<HermesExecution?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}
