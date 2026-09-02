using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface ILearningStore
{
    /// <summary>Append-only — learning is incremental, never overwritten.</summary>
    Task<Lesson> RecordAsync(
        Lesson lesson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Lesson>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
