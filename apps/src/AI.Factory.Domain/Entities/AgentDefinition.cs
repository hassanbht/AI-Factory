using AI.Factory.Domain.Common;

namespace AI.Factory.Domain.Entities;

/// <summary>
/// WHAT an agent is: its logical definition/configuration, independent of any
/// particular execution. One AgentDefinition produces many <see cref="AgentRun"/>s.
/// </summary>
public sealed class AgentDefinition : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Logical role this agent performs (e.g. "Coding", "Review", "Planning") — free text on purpose, no fixed taxonomy yet.</summary>
    public string AgentType { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public long Version { get; set; } = 1;

    public ICollection<AgentRun> Runs { get; set; } = new List<AgentRun>();
}
