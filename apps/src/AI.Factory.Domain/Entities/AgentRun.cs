namespace AI.Factory.Domain.Entities;

public sealed class AgentRun
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
