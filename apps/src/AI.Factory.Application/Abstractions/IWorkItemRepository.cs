using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IWorkItemRepository
{
    Task<IReadOnlyList<WorkItem>> GetByProjectIdAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<WorkItem?> GetByIdAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default);

    Task AddDependencyAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveDependencyAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default);

    Task<bool> DependencyExistsAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default);
}
