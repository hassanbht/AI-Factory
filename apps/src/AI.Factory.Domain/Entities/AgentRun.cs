using AI.Factory.Domain.Common;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;

namespace AI.Factory.Domain.Entities;

/// <summary>
/// WHICH execution of an <see cref="AgentDefinition"/> this is. The canonical,
/// runtime-agnostic orchestration-level execution record — reusable across
/// analysis/planning/coding/review/repair/validation runs, not tied to any
/// particular runtime (Hermes, or a future replacement) or to WorkItem being
/// its identity. Physical Hermes process execution detail lives separately
/// in <see cref="HermesExecution"/>.
/// </summary>
public sealed class AgentRun : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentDefinitionId { get; set; }

    public AgentDefinition? AgentDefinition { get; set; }

    public Guid? ProjectId { get; set; }

    // WorkItemId is a plain scalar reference, not an EF relationship — no
    // navigation property on purpose (see FactoryDbContext: an AgentRun's own
    // history must never vanish because of an unrelated WorkItem cascade
    // delete, matching the OrchestrationRun precedent).
    public Guid? WorkItemId { get; set; }

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Created;

    public int Iteration { get; set; }

    public int Attempt { get; set; } = 1;

    /// <summary>Identifies one logical execution request — the idempotency key. Unique per row; a repeated request with the same CorrelationId must resolve to this same AgentRun, never create a second one.</summary>
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? Input { get; set; }

    /// <summary>Output summary/reference — large runtime payloads belong in the Artifacts store, not inline here.</summary>
    public string? Output { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    public string? ErrorMessage { get; set; }

    public FailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// Serialized structured-agent-result envelope (versioned JSON), when one was
    /// produced/validated for this run — null for runs predating that contract or
    /// where no structured output was attempted. Kept as a plain nullable JSON
    /// column (not a dedicated table) deliberately: one row per AgentRun, no
    /// join, no event-sourcing machinery, and SQLite/EF-migration friendly.
    /// </summary>
    public string? StructuredResult { get; set; }

    /// <summary>Envelope version of <see cref="StructuredResult"/>, denormalized for cheap filtering without deserializing the JSON blob.</summary>
    public int? StructuredResultVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public long Version { get; set; } = 1;

    /// <summary>Applies a lifecycle transition, throwing <see cref="InvalidAgentRunTransitionException"/> if not allowed from the current status. The only sanctioned way to mutate Status.</summary>
    public void TransitionTo(AgentRunStatus next)
    {
        if (!AgentRunTransitions.IsValid(Status, next))
        {
            throw new InvalidAgentRunTransitionException(Status, next);
        }

        Status = next;
        UpdatedAtUtc = DateTime.UtcNow;

        if (next == AgentRunStatus.Running && StartedAtUtc is null)
        {
            StartedAtUtc = DateTime.UtcNow;
        }

        if (AgentRunTransitions.IsTerminal(next))
        {
            CompletedAtUtc = DateTime.UtcNow;
        }
    }
}
