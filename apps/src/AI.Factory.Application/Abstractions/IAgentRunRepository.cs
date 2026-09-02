using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IAgentRunRepository
{
    /// <summary>Persists an AgentRun row before execution starts (the audit trail must exist before the risky operation).</summary>
    Task CreatePendingAsync(
        AgentRun agentRun,
        CancellationToken cancellationToken = default);

    /// <summary>Updates a previously-created AgentRun with its outcome.</summary>
    Task CompleteAsync(
        Guid agentRunId,
        bool success,
        string? output,
        string? error,
        long durationMs,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent agent run for a WorkItem, or null if none exists yet — used to feed a previous-attempt summary into a repair prompt.</summary>
    Task<AgentRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}
