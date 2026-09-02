using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IReviewContextBuilder
{
    Task<ReviewContext> BuildAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default);
}
