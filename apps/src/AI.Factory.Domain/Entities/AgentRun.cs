using System.ComponentModel.DataAnnotations.Schema;
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

    /// <summary>
    /// Phase 18.14 — the Factory-resolved, trusted execution workspace for
    /// this run. Transient/execution-time-only, exactly like the non-persisted
    /// snapshot <see cref="AgentRun"/> that AgentOrchestrator.BuildExecutionSnapshot
    /// already constructs to carry Input to IAgentRuntime: [NotMapped] means EF
    /// Core excludes it from the model entirely, so nothing about workspace
    /// resolution survives a restart in the database — it is re-derived fresh
    /// from ProjectId -> Project.RootPath on every execution, including recovery.
    /// </summary>
    [NotMapped]
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Phase D.1 — Authoritative execution timeout at the Factory boundary.
    /// Derived from Worker execution budget minus safety margin or ExecutionLimitsOptions.
    /// Transient/execution-time-only; excluded from persistence.
    /// </summary>
    [NotMapped]
    public TimeSpan? ExecutionTimeout { get; set; }

    /// <summary>
    /// Phase D.1 — Absolute execution deadline derived from startedAt + effectiveTimeout.
    /// All lower-level operations derive remaining time from this deadline.
    /// Transient/execution-time-only; excluded from persistence.
    /// </summary>
    [NotMapped]
    public DateTimeOffset? ExecutionDeadline { get; set; }

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
