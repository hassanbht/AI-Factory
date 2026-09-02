using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class EvaluationRunRepository : IEvaluationRunRepository
{
    private readonly FactoryDbContext _db;

    public EvaluationRunRepository(FactoryDbContext db)
    {
        _db = db;
    }

    public async Task PersistAsync(
        EvaluationRun evaluationRun,
        CancellationToken cancellationToken = default)
    {
        await _db.EvaluationRuns.AddAsync(evaluationRun, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
