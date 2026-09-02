using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class EvaluationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkItemId { get; set; }

    public WorkItem? WorkItem { get; set; }

    public bool BuildSucceeded { get; set; }

    public bool? TestsSucceeded { get; set; }

    public bool ReviewBlocked { get; set; }

    public ApprovalDecision? HumanApproval { get; set; }

    public int RepairCount { get; set; }

    public string Regression { get; set; } = string.Empty;

    public bool IsFullyValidated { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
