using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Snapshot of the project's workflow status this approval was raised from.</summary>
    public ProjectStatus Workflow { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public string RequestedBy { get; set; } = string.Empty;

    public ApprovalDecision Decision { get; set; } = ApprovalDecision.PendingApproval;

    public DateTime? DecidedAtUtc { get; set; }

    public string? Comment { get; set; }
}
