using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>Central, deterministic AgentRun state-transition table — the only place valid transitions are defined, so no unrelated code can mutate Status arbitrarily.</summary>
public static class AgentRunTransitions
{
    private static readonly IReadOnlyDictionary<AgentRunStatus, IReadOnlyCollection<AgentRunStatus>> Allowed =
        new Dictionary<AgentRunStatus, IReadOnlyCollection<AgentRunStatus>>
        {
            [AgentRunStatus.Created] = [AgentRunStatus.Queued],
            [AgentRunStatus.Queued] = [AgentRunStatus.Running, AgentRunStatus.Cancelled],
            [AgentRunStatus.Running] =
            [
                AgentRunStatus.Waiting,
                AgentRunStatus.Succeeded,
                AgentRunStatus.Failed,
                AgentRunStatus.TimedOut,
                AgentRunStatus.Cancelled
            ],
            [AgentRunStatus.Waiting] =
            [
                AgentRunStatus.Running,
                AgentRunStatus.Failed,
                AgentRunStatus.Cancelled
            ],
            [AgentRunStatus.Succeeded] = [],
            [AgentRunStatus.Failed] = [],
            [AgentRunStatus.TimedOut] = [],
            [AgentRunStatus.Cancelled] = []
        };

    private static readonly IReadOnlyCollection<AgentRunStatus> TerminalStatuses =
    [
        AgentRunStatus.Succeeded,
        AgentRunStatus.Failed,
        AgentRunStatus.TimedOut,
        AgentRunStatus.Cancelled
    ];

    public static bool IsValid(AgentRunStatus from, AgentRunStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(AgentRunStatus status) => TerminalStatuses.Contains(status);
}
