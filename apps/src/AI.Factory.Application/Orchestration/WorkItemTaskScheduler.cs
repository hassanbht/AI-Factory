using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class WorkItemTaskScheduler : ITaskScheduler
{
    private static readonly IReadOnlyDictionary<string, int> PriorityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Critical"] = 0,
            ["High"] = 1,
            ["Normal"] = 2,
            ["Low"] = 3
        };

    private readonly IWorkItemRepository _workItems;
    private readonly ITaskDependencyResolver _resolver;

    public WorkItemTaskScheduler(
        IWorkItemRepository workItems,
        ITaskDependencyResolver resolver)
    {
        _workItems = workItems;
        _resolver = resolver;
    }

    public async Task<WorkItem?> GetNextReadyWorkItemAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var items = await _workItems.GetByProjectIdAsync(projectId, cancellationToken);

        if (items.Any(x => x.Status == WorkItemStatus.Running.ToString()))
        {
            return null;
        }

        var readiness = _resolver.Resolve(items);

        return items
            .Where(x => readiness[x.Id] == TaskReadinessStatus.Ready)
            .OrderBy(x => GetPriorityRank(x.Priority))
            .ThenBy(x => x.CreatedAtUtc)
            .FirstOrDefault();
    }

    private static int GetPriorityRank(string priority)
    {
        return PriorityRank.TryGetValue(priority, out var rank)
            ? rank
            : PriorityRank["Normal"];
    }
}
