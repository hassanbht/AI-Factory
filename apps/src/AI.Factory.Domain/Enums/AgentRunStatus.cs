namespace AI.Factory.Domain.Enums;

/// <summary>Lifecycle status of a canonical <see cref="Entities.AgentRun"/>. Transitions are enforced centrally by <see cref="Entities.AgentRunTransitions"/>.</summary>
public enum AgentRunStatus
{
    Created,
    Queued,
    Running,
    Waiting,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}
