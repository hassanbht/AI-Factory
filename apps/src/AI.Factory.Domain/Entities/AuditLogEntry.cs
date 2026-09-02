using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class AuditLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExecutionId { get; set; }

    public Guid? ProjectId { get; set; }

    public Guid? WorkItemId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public AuditEventType EventType { get; set; }

    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

    public string Message { get; set; } = string.Empty;

    /// <summary>JSON object of extra structured fields — never secrets/API keys/tokens/credentials; AuditLogger filters those out before this is set.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Ties this entry to the operation-correlation mechanism (Phase 17.3). Nullable for schema safety on migration; AuditLogger always populates it (from an explicit value, the ambient IOperationContext, or falling back to ExecutionId) for entries written going forward.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>The specific operation instance (IOperationScope) that produced this entry, if any.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>The parent operation this one was nested under, if any.</summary>
    public Guid? ParentOperationId { get; set; }

    /// <summary>The OrchestrationRun this entry belongs to, when known.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Free-form business outcome ("Success"/"Failure"/"Partial") — distinct from Severity, which is a log level, not a business result.</summary>
    public string? Outcome { get; set; }

    /// <summary>Who/what caused this event — "System", "Agent", or "Human".</summary>
    public string ActorType { get; set; } = "System";

    /// <summary>Identifier of the actor when known (e.g. an approver name) — never fabricated when not available.</summary>
    public string? ActorId { get; set; }

    /// <summary>Set only for failure-severity entries whose cause was classified via IFailureClassifier — never fabricated for entries that aren't failures.</summary>
    public FailureCategory? FailureCategory { get; set; }
}
