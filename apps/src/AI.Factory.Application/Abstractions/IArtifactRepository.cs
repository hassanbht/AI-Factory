using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IArtifactRepository
{
    Task<IReadOnlyList<Artifact>> GetByWorkItemIdAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}
