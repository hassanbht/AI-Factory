namespace AI.Factory.Domain.Entities;

/// <summary>
/// One physical Hermes CLI process invocation attempt against a WorkItem.
/// This is infrastructure-level execution history (how a task was physically
/// run), distinct from the canonical orchestration-level <see cref="AgentRun"/>,
/// which represents a stateful agent execution lifecycle independent of any
/// particular runtime. Formerly named AgentRun before Phase 18.1.2 split the
/// two responsibilities apart.
/// </summary>
public sealed class HermesExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public string Agent { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? Provider { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? Response { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public int AttemptNumber { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public long? DurationMs { get; set; }
}
