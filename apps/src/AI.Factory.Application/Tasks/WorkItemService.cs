using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Tasks;

/// <summary>
/// Application-layer use case for WorkItem ("Task") CRUD + lifecycle,
/// mirroring ProjectService's shape. Wraps the existing IWorkItemRepository —
/// does not duplicate TaskStateManager/WorkItemTaskScheduler, which remain
/// the execution-time state machine for the AutonomousCodingOrchestrator path.
/// </summary>
public sealed class WorkItemService
{
    private readonly IWorkItemRepository _workItems;
    private readonly IProjectRepository _projects;

    public WorkItemService(IWorkItemRepository workItems, IProjectRepository projects)
    {
        _workItems = workItems;
        _projects = projects;
    }

    public async Task<WorkItem> CreateAsync(
        Guid projectId, WorkItemType type, string title, string? description, string priority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ValidationException("Task title is required.");
        }

        var project = await _projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

        var now = DateTime.UtcNow;

        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Type = type,
            Title = title,
            Description = description ?? string.Empty,
            Status = WorkItemStatus.Draft.ToString(),
            Priority = priority,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _workItems.AddAsync(workItem, cancellationToken);
        return workItem;
    }

    public Task<WorkItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _workItems.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<WorkItem>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _workItems.GetByProjectIdAsync(projectId, cancellationToken);

    public async Task<WorkItem> TransitionStatusAsync(
        Guid id, WorkItemStatus next, CancellationToken cancellationToken = default)
    {
        var workItem = await _workItems.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Task '{id}' was not found.");

        workItem.TransitionTo(next);
        await _workItems.UpdateAsync(workItem, cancellationToken);
        return workItem;
    }
}
