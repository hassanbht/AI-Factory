using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class ReviewRunRepository : IReviewRunRepository
{
    private readonly FactoryDbContext _db;

    public ReviewRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task PersistAsync(
        ReviewRun reviewRun,
        CancellationToken cancellationToken = default)
    {
        await _db.ReviewRuns.AddAsync(reviewRun, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReviewRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ReviewRuns
            .Where(x => x.WorkItemId == workItemId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
