using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Tasks;

/// <summary>
/// Write-side validation for WorkItemDependency edges — the persisted
/// dependency table and read-side cycle/readiness classification already
/// exist (WorkItemDependency, ITaskDependencyResolver). This is the missing
/// piece: reject self/duplicate/cross-project/cycle-introducing edges
/// BEFORE they're written, per Phase 18.2 §7.
/// </summary>
public sealed class TaskDependencyService
{
    private readonly IWorkItemRepository _workItems;
    private readonly ITaskDependencyResolver _resolver;

    public TaskDependencyService(IWorkItemRepository workItems, ITaskDependencyResolver resolver)
    {
        _workItems = workItems;
        _resolver = resolver;
    }

    public async Task AddDependencyAsync(
        Guid workItemId, Guid dependsOnWorkItemId, CancellationToken cancellationToken = default)
    {
        if (workItemId == dependsOnWorkItemId)
        {
            throw new ValidationException("A task cannot depend on itself.");
        }

        var workItem = await _workItems.GetByIdAsync(workItemId, cancellationToken)
            ?? throw new NotFoundException($"Task '{workItemId}' was not found.");

        var dependsOn = await _workItems.GetByIdAsync(dependsOnWorkItemId, cancellationToken)
            ?? throw new NotFoundException($"Task '{dependsOnWorkItemId}' was not found.");

        if (workItem.ProjectId != dependsOn.ProjectId)
        {
            throw new ValidationException("A task can only depend on another task in the same project.");
        }

        if (await _workItems.DependencyExistsAsync(workItemId, dependsOnWorkItemId, cancellationToken))
        {
            throw new ConflictException($"Task '{workItemId}' already depends on '{dependsOnWorkItemId}'.");
        }

        // Simulate the edge in-memory against the full project graph before
        // persisting — the resolver already does DFS cycle detection; reuse
        // it rather than re-implementing the algorithm.
        var projectItems = await _workItems.GetByProjectIdAsync(workItem.ProjectId, cancellationToken);
        var mutableItem = projectItems.First(x => x.Id == workItemId);
        var simulatedEdge = new WorkItemDependency { WorkItemId = workItemId, DependsOnWorkItemId = dependsOnWorkItemId };
        mutableItem.Dependencies.Add(simulatedEdge);

        try
        {
            _resolver.Resolve(projectItems);
        }
        catch (CircularWorkItemDependencyException ex)
        {
            throw new ValidationException($"Adding this dependency would create a circular dependency: {ex.Message}");
        }
        finally
        {
            // projectItems came from this same EF context (tracked) — the
            // simulated edge must not leak into the change tracker, or the
            // real insert below collides on the same {WorkItemId,
            // DependsOnWorkItemId} key.
            mutableItem.Dependencies.Remove(simulatedEdge);
        }

        await _workItems.AddDependencyAsync(workItemId, dependsOnWorkItemId, cancellationToken);
    }

    public async Task RemoveDependencyAsync(
        Guid workItemId, Guid dependsOnWorkItemId, CancellationToken cancellationToken = default)
    {
        var removed = await _workItems.RemoveDependencyAsync(workItemId, dependsOnWorkItemId, cancellationToken);

        if (!removed)
        {
            throw new NotFoundException(
                $"Task '{workItemId}' has no dependency on '{dependsOnWorkItemId}'.");
        }
    }
}
