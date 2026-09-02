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
}
