using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IReviewRunRepository
{
    Task PersistAsync(
        ReviewRun reviewRun,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent review run for a WorkItem, or null if none exists yet.</summary>
    Task<ReviewRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}
