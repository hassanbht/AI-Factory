using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface IValidationRunRepository
{
    /// <summary>Every validation run must be persisted — this is the only write path.</summary>
    Task PersistAsync(
        ValidationRun validationRun,
        CancellationToken cancellationToken = default);

    /// <summary>Most recent validation run for a WorkItem, or null if none exists yet.</summary>
    Task<ValidationRun?> GetLatestAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>Full validation history for a WorkItem, oldest first — used to detect regressions between attempts.</summary>
    Task<IReadOnlyList<ValidationRun>> GetHistoryAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}
