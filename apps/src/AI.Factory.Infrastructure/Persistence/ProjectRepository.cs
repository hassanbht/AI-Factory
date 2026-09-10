using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class ProjectRepository : IProjectRepository
{
    private readonly FactoryDbContext _db;

    public ProjectRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<Project?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var existing = _db.ChangeTracker.Entries<Project>().FirstOrDefault(e => e.Entity.Id == id);
        if (existing is not null)
        {
            await existing.ReloadAsync(cancellationToken);
            return existing.Entity;
        }

        return await _db.Projects
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);
    }

    public Task<Project?> GetBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        return _db.Projects
            .FirstOrDefaultAsync(
                x => x.Slug == slug,
                cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Projects
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        await _db.Projects.AddAsync(
            project,
            cancellationToken);
    }

    public Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
