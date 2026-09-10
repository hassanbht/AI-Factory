using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Exceptions;

/// <summary>Thrown when code attempts a WorkItem status transition not permitted by <see cref="Entities.WorkItemTransitions"/>.</summary>
public sealed class InvalidWorkItemTransitionException : InvalidOperationException
{
    public WorkItemStatus From { get; }
    public WorkItemStatus To { get; }

    public InvalidWorkItemTransitionException(WorkItemStatus from, WorkItemStatus to)
        : base($"Invalid WorkItem transition from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }
}
