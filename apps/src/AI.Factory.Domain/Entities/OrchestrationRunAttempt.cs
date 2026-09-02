using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>One row per attempt, append-only — never updated after CompletedAtUtc is set, so the full history survives even after a later attempt succeeds.</summary>
public sealed class OrchestrationRunAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrchestrationRunId { get; set; }

    public OrchestrationRun? OrchestrationRun { get; set; }

    public int AttemptNumber { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public bool Success { get; set; }

    public string? AgentResultSummary { get; set; }

    public string? BuildResultSummary { get; set; }

    public string? TestResultSummary { get; set; }

    public string? ValidationResultSummary { get; set; }

    public string? RepairContextText { get; set; }

    public string? ReviewResultSummary { get; set; }

    public OrchestrationFailureCategory? FailureCategory { get; set; }

    public string? ErrorMessage { get; set; }
}
