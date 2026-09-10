using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Exceptions;

/// <summary>Thrown when code attempts an AgentRun status transition not permitted by <see cref="Entities.AgentRunTransitions"/>.</summary>
public sealed class InvalidAgentRunTransitionException : InvalidOperationException
{
    public AgentRunStatus From { get; }

    public AgentRunStatus To { get; }

    public InvalidAgentRunTransitionException(AgentRunStatus from, AgentRunStatus to)
        : base($"Invalid AgentRun transition from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }
}
