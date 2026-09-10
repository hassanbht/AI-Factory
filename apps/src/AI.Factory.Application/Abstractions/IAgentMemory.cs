namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Reads/writes agent-relevant contextual memory — NOT Factory workflow
/// state (that stays in AgentRun/WorkItem etc.). Provider-neutral: a local
/// SQLite-backed implementation and a future OpenViking adapter both
/// implement this without callers changing. Scope is expressed by which of
/// ProjectId/AgentDefinitionId/AgentRunId a query supplies — omitting all
/// three is an explicit request for global memory, never a leaky default,
/// and a provider must enforce the requested scope itself rather than
/// relying on callers to filter correctly.
/// </summary>
public interface IAgentMemory
{
    Task StoreAsync(
        AgentMemoryEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
        AgentMemoryQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record AgentMemoryEntry(
    Guid Id,
    Guid? ProjectId,
    Guid? AgentDefinitionId,
    Guid? AgentRunId,
    string Type,
    string Content,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTime CreatedAtUtc);

public sealed record AgentMemoryQuery(
    Guid? ProjectId = null,
    Guid? AgentDefinitionId = null,
    Guid? AgentRunId = null,
    string? Type = null,
    string? SearchText = null,
    int MaxResults = 20);
