namespace AI.Factory.Domain.Enums;

/// <summary>
/// Computed readiness classification for a WorkItem, derived from its own
/// status plus its dependency graph. Never persisted — always recomputed
/// from ground-truth <see cref="WorkItemStatus"/> values and
/// <c>WorkItemDependency</c> edges.
/// </summary>
public enum TaskReadinessStatus
{
    Ready,
    WaitingForDependency,
    BlockedByFailedDependency,
    Running,
    Completed,
    Cancelled,
    Failed
}
