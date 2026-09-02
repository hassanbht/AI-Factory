using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

public interface ITaskScheduler
{
    /// <summary>
    /// Returns the single next WorkItem eligible to run for the given
    /// project, or null if none is ready. Concurrency = 1: returns null if
    /// any WorkItem in the project is already Running.
    /// </summary>
    Task<WorkItem?> GetNextReadyWorkItemAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
