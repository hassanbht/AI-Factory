using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Exceptions;

/// <summary>Thrown when code attempts a Project status transition not permitted by <see cref="Entities.ProjectTransitions"/>.</summary>
public sealed class InvalidProjectTransitionException : InvalidOperationException
{
    public ProjectStatus From { get; }
    public ProjectStatus To { get; }

    public InvalidProjectTransitionException(ProjectStatus from, ProjectStatus to)
        : base($"Invalid Project transition from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }
}
