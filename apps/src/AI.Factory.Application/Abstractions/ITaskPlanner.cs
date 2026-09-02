using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface ITaskPlanner
{
    /// <summary>
    /// Transforms an approved specification into an executable, but not yet
    /// persisted, WorkItem graph. Never touches the database — a separate
    /// application service persists an approved TaskPlan.
    /// </summary>
    Task<TaskPlan> CreatePlanAsync(
        TaskPlanRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TaskPlanRequest(
    Guid ProjectId,
    string SpecificationTitle,
    string SpecificationContent);

public sealed record PlannedTask(
    Guid Id,
    string Title,
    string Description,
    WorkItemType Type,
    string Priority,
    IReadOnlyList<Guid> Dependencies,
    string? AcceptanceCriteria,
    TaskComplexity EstimatedComplexity);

public sealed record TaskPlan(
    Guid ProjectId,
    IReadOnlyList<PlannedTask> Tasks);

/// <summary>Thrown when a TaskPlan fails structural validation (see TaskPlanValidator).</summary>
public sealed class InvalidTaskPlanException : Exception
{
    public InvalidTaskPlanException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when an ITaskPlanner cannot produce a valid plan (agent failure, or malformed output after all retries).</summary>
public sealed class TaskPlanningException : Exception
{
    public TaskPlanningException(string message)
        : base(message)
    {
    }

    public TaskPlanningException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
