using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface ITaskDependencyResolver
{
    /// <summary>
    /// Computes the <see cref="TaskReadinessStatus"/> of every item in
    /// <paramref name="workItems"/>, based on each item's own status and the
    /// status of the items it depends on (via <see cref="WorkItem.Dependencies"/>).
    /// </summary>
    /// <exception cref="CircularWorkItemDependencyException">
    /// Thrown when the dependency edges among <paramref name="workItems"/> contain a cycle.
    /// </exception>
    IReadOnlyDictionary<Guid, TaskReadinessStatus> Resolve(
        IReadOnlyCollection<WorkItem> workItems);
}

public sealed class CircularWorkItemDependencyException : Exception
{
    public CircularWorkItemDependencyException(string message)
        : base(message)
    {
    }
}
