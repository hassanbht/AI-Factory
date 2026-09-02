using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class LearningStore : ILearningStore
{
    private readonly FactoryDbContext _db;

    public LearningStore(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task<Lesson> RecordAsync(
        Lesson lesson,
        CancellationToken cancellationToken = default)
    {
        await _db.Lessons.AddAsync(lesson, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return lesson;
    }

    public async Task<IReadOnlyList<Lesson>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Lessons
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
