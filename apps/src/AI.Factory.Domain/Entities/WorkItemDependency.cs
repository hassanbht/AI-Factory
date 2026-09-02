namespace AI.Factory.Domain.Entities;

public sealed class WorkItemDependency
{
    public Guid WorkItemId { get; set; }

    public WorkItem WorkItem { get; set; } = null!;

    public Guid DependsOnWorkItemId { get; set; }

    public WorkItem DependsOnWorkItem { get; set; } = null!;
}
