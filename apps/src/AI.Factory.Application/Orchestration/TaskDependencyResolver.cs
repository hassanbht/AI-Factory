using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class TaskDependencyResolver : ITaskDependencyResolver
{
    private enum VisitState
    {
        Unvisited,
        Visiting,
        Visited
    }

    public IReadOnlyDictionary<Guid, TaskReadinessStatus> Resolve(
        IReadOnlyCollection<WorkItem> workItems)
    {
        var byId = workItems.ToDictionary(x => x.Id);

        DetectCycles(workItems, byId);

        var result = new Dictionary<Guid, TaskReadinessStatus>();

        foreach (var item in workItems)
        {
            result[item.Id] = Classify(item, byId);
        }

        return result;
    }

    private static TaskReadinessStatus Classify(
        WorkItem item,
        IReadOnlyDictionary<Guid, WorkItem> byId)
    {
        if (item.Status == WorkItemStatus.Running.ToString())
        {
            return TaskReadinessStatus.Running;
        }

        if (item.Status == WorkItemStatus.Completed.ToString())
        {
            return TaskReadinessStatus.Completed;
        }

        if (item.Status == WorkItemStatus.Cancelled.ToString())
        {
            return TaskReadinessStatus.Cancelled;
        }

        if (item.Status == WorkItemStatus.Failed.ToString())
        {
            return TaskReadinessStatus.Failed;
        }

        if (item.Dependencies.Count == 0)
        {
            return TaskReadinessStatus.Ready;
        }

        var anyFailed = false;
        var allCompleted = true;

        foreach (var dependency in item.Dependencies)
        {
            if (!byId.TryGetValue(dependency.DependsOnWorkItemId, out var target))
            {
                allCompleted = false;
                continue;
            }

            if (target.Status == WorkItemStatus.Failed.ToString())
            {
                anyFailed = true;
            }

            if (target.Status != WorkItemStatus.Completed.ToString())
            {
                allCompleted = false;
            }
        }

        if (anyFailed)
        {
            return TaskReadinessStatus.BlockedByFailedDependency;
        }

        return allCompleted
            ? TaskReadinessStatus.Ready
            : TaskReadinessStatus.WaitingForDependency;
    }

    private static void DetectCycles(
        IReadOnlyCollection<WorkItem> workItems,
        IReadOnlyDictionary<Guid, WorkItem> byId)
    {
        var state = workItems.ToDictionary(x => x.Id, _ => VisitState.Unvisited);

        foreach (var item in workItems)
        {
            if (state[item.Id] == VisitState.Unvisited)
            {
                Visit(item.Id, byId, state);
            }
        }
    }

    private static void Visit(
        Guid id,
        IReadOnlyDictionary<Guid, WorkItem> byId,
        Dictionary<Guid, VisitState> state)
    {
        state[id] = VisitState.Visiting;

        if (byId.TryGetValue(id, out var item))
        {
            foreach (var dependency in item.Dependencies)
            {
                var targetId = dependency.DependsOnWorkItemId;

                if (!state.TryGetValue(targetId, out var targetState))
                {
                    continue;
                }

                if (targetState == VisitState.Visiting)
                {
                    throw new CircularWorkItemDependencyException(
                        $"Circular dependency detected involving WorkItem '{targetId}'.");
                }

                if (targetState == VisitState.Unvisited)
                {
                    Visit(targetId, byId, state);
                }
            }
        }

        state[id] = VisitState.Visited;
    }
}
