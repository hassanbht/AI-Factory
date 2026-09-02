using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class WorkItemRepository : IWorkItemRepository
{
    private readonly FactoryDbContext _db;

    public WorkItemRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<WorkItem>> GetByProjectIdAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await _db.WorkItems
            .Where(x => x.ProjectId == projectId)
            .Include(x => x.Dependencies)
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkItem?> GetByIdAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.WorkItems
            .FirstOrDefaultAsync(x => x.Id == workItemId, cancellationToken);
    }

    public async Task UpdateAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        // No-op if already tracked (EF identity map) — SaveChanges alone
        // persists whatever the caller mutated; Update() only matters when
        // the instance came from outside this DbContext's tracking.
        if (_db.Entry(workItem).State == EntityState.Detached)
        {
            _db.WorkItems.Update(workItem);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
