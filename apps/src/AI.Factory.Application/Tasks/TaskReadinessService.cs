using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Tasks;

/// <summary>
/// Answers "can this one task execute now?" deterministically from persisted
/// state only — no LLM/Hermes call. Thin wrapper over the existing
/// ITaskDependencyResolver (which already classifies a whole project's
/// WorkItems); this just resolves the full project graph and returns the
/// single requested item's classification. Foundation for a future
/// scheduler, per Phase 18.2 §11 — not the scheduler itself.
/// </summary>
public sealed class TaskReadinessService
{
    private readonly IWorkItemRepository _workItems;
    private readonly ITaskDependencyResolver _resolver;

    public TaskReadinessService(IWorkItemRepository workItems, ITaskDependencyResolver resolver)
    {
        _workItems = workItems;
        _resolver = resolver;
    }

    public async Task<TaskReadinessStatus> GetReadinessAsync(
        Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _workItems.GetByIdAsync(workItemId, cancellationToken)
            ?? throw new NotFoundException($"Task '{workItemId}' was not found.");

        var projectItems = await _workItems.GetByProjectIdAsync(workItem.ProjectId, cancellationToken);
        var readiness = _resolver.Resolve(projectItems);

        return readiness[workItemId];
    }
}
