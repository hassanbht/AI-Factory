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
        var existing = _db.ChangeTracker.Entries<WorkItem>().FirstOrDefault(e => e.Entity.Id == workItemId);
        if (existing is not null)
        {
            await existing.ReloadAsync(cancellationToken);
            return existing.Entity;
        }

        return await _db.WorkItems
            .Include(x => x.Dependencies)
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

    public async Task AddAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        await _db.WorkItems.AddAsync(workItem, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddDependencyAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default)
    {
        await _db.WorkItemDependencies.AddAsync(
            new WorkItemDependency { WorkItemId = workItemId, DependsOnWorkItemId = dependsOnWorkItemId },
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RemoveDependencyAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.WorkItemDependencies.FirstOrDefaultAsync(
            x => x.WorkItemId == workItemId && x.DependsOnWorkItemId == dependsOnWorkItemId,
            cancellationToken);

        if (existing is null)
        {
            return false;
        }

        _db.WorkItemDependencies.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<bool> DependencyExistsAsync(
        Guid workItemId,
        Guid dependsOnWorkItemId,
        CancellationToken cancellationToken = default)
    {
        return _db.WorkItemDependencies.AnyAsync(
            x => x.WorkItemId == workItemId && x.DependsOnWorkItemId == dependsOnWorkItemId,
            cancellationToken);
    }
}
