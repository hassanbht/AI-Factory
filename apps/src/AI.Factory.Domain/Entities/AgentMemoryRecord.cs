namespace AI.Factory.Domain.Entities;

/// <summary>
/// Persisted row backing <c>IAgentMemory</c>'s local provider. Contextual
/// knowledge only — never Factory workflow state (see AgentRun/WorkItem for
/// that). ProjectId/AgentDefinitionId/AgentRunId express scope: all null
/// means global memory.
/// </summary>
public sealed class AgentMemoryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }

    public Guid? AgentDefinitionId { get; set; }

    public Guid? AgentRunId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>Metadata dictionary serialized as JSON — kept a plain string so this entity has no serializer dependency.</summary>
    public string? MetadataJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
