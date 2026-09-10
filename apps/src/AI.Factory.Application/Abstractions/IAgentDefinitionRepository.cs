using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>Persists <see cref="AgentDefinition"/> rows — the logical configuration an AgentRun executes against.</summary>
public interface IAgentDefinitionRepository
{
    Task<AgentDefinition> CreateAsync(
        AgentDefinition agentDefinition,
        CancellationToken cancellationToken = default);

    Task<AgentDefinition?> GetByIdAsync(
        Guid agentDefinitionId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        AgentDefinition agentDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>All definitions, ordered by Name — the catalog is small enough that no paging is needed yet.</summary>
    Task<IReadOnlyList<AgentDefinition>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
