using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

public sealed class WorkflowTransition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public ProjectStatus FromStatus { get; set; }

    public ProjectStatus ToStatus { get; set; }

    public string? Reason { get; set; }

    public string? Actor { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
