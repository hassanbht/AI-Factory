using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class ValidationRunRepository : IValidationRunRepository
{
    private readonly FactoryDbContext _db;

    public ValidationRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task PersistAsync(
        ValidationRun validationRun,
        CancellationToken cancellationToken = default)
    {
        await _db.ValidationRuns.AddAsync(validationRun, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ValidationRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ValidationRuns
            .Where(x => x.WorkItemId == workItemId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ValidationRun>> GetHistoryAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ValidationRuns
            .Where(x => x.WorkItemId == workItemId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
