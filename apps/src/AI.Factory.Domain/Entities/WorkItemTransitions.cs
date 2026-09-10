using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>
/// Central, deterministic WorkItem state-transition table, mirroring
/// AgentRunTransitions. Operates on the WorkItemStatus enum even though
/// WorkItem.Status is persisted as a plain string (existing convention,
/// unchanged) — WorkItem.TransitionTo parses/validates/writes through this.
/// </summary>
public static class WorkItemTransitions
{
    private static readonly IReadOnlyDictionary<WorkItemStatus, IReadOnlyCollection<WorkItemStatus>> Allowed =
        new Dictionary<WorkItemStatus, IReadOnlyCollection<WorkItemStatus>>
        {
            [WorkItemStatus.Draft] = [WorkItemStatus.Ready, WorkItemStatus.Cancelled],
            [WorkItemStatus.Ready] = [WorkItemStatus.Running, WorkItemStatus.Blocked, WorkItemStatus.Cancelled],
            [WorkItemStatus.Running] =
            [
                WorkItemStatus.Testing, WorkItemStatus.Blocked, WorkItemStatus.Completed,
                WorkItemStatus.Failed, WorkItemStatus.Cancelled
            ],
            [WorkItemStatus.Testing] =
            [
                WorkItemStatus.Repairing, WorkItemStatus.Reviewing, WorkItemStatus.Completed,
                WorkItemStatus.Failed, WorkItemStatus.Cancelled
            ],
            [WorkItemStatus.Repairing] = [WorkItemStatus.Testing, WorkItemStatus.Failed, WorkItemStatus.Cancelled],
            [WorkItemStatus.Reviewing] =
            [
                WorkItemStatus.Completed, WorkItemStatus.Repairing, WorkItemStatus.Failed, WorkItemStatus.Cancelled
            ],
            [WorkItemStatus.Blocked] = [WorkItemStatus.Ready, WorkItemStatus.Cancelled],
            [WorkItemStatus.Completed] = [],
            [WorkItemStatus.Failed] = [],
            [WorkItemStatus.Cancelled] = []
        };

    private static readonly IReadOnlyCollection<WorkItemStatus> TerminalStatuses =
        [WorkItemStatus.Completed, WorkItemStatus.Failed, WorkItemStatus.Cancelled];

    public static bool IsValid(WorkItemStatus from, WorkItemStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(WorkItemStatus status) => TerminalStatuses.Contains(status);
}
