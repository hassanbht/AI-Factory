using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>
/// One paired start/complete (or fail/skip) record for a single stage of a
/// single OrchestrationRun attempt — the persistent execution trace. A given
/// (Stage, AttemptNumber) pair can legitimately repeat across attempts (e.g.
/// Build attempt 1, Build attempt 2, ...), so this is append-only like
/// OrchestrationRunAttempt, never reused across attempts.
/// </summary>
public sealed class OrchestrationRunStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrchestrationRunId { get; set; }

    public OrchestrationRun? OrchestrationRun { get; set; }

    public OrchestrationStepKind Stage { get; set; }

    public OrchestrationStepStatus Status { get; set; } = OrchestrationStepStatus.Pending;

    /// <summary>Monotonic per-run ordering assigned at persistence time — timestamps alone can tie, insertion order is not a documented DB guarantee.</summary>
    public int Sequence { get; set; }

    public int? AttemptNumber { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public string? FailureType { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>Simple JSON object (e.g. {"model":"...","provider":"..."}) — nullable, never fabricated when the underlying data isn't available.</summary>
    public string? MetadataJson { get; set; }
}
