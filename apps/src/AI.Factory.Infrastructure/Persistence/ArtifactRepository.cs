using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class ArtifactRepository : IArtifactRepository
{
    private readonly FactoryDbContext _db;

    public ArtifactRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Artifact>> GetByWorkItemIdAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Artifacts
            .Where(x => x.WorkItemId == workItemId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<Artifact?> GetByIdAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Artifacts
            .FirstOrDefaultAsync(x => x.Id == artifactId, cancellationToken);
    }
}
