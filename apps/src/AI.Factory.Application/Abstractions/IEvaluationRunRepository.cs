using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IEvaluationRunRepository
{
    Task PersistAsync(
        EvaluationRun evaluationRun,
        CancellationToken cancellationToken = default);
}
