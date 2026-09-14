using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>Central, deterministic Project state-transition table, mirroring AgentRunTransitions.</summary>
public static class ProjectTransitions
{
    private static readonly IReadOnlyDictionary<ProjectStatus, IReadOnlyCollection<ProjectStatus>> Allowed =
        new Dictionary<ProjectStatus, IReadOnlyCollection<ProjectStatus>>
        {
            [ProjectStatus.Created] = [ProjectStatus.Analyzing, ProjectStatus.Cancelled],
            [ProjectStatus.Analyzing] = [ProjectStatus.Specification, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Specification] = [ProjectStatus.Architecture, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Architecture] = [ProjectStatus.Planning, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Planning] = [ProjectStatus.Implementation, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Implementation] = [ProjectStatus.Testing, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Testing] = [ProjectStatus.Repairing, ProjectStatus.Reviewing, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Repairing] = [ProjectStatus.Testing, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            // Phase 18.11 — HumanReview is the sole authoritative approval
            // boundary for the new Project lifecycle: Completed is reachable
            // only from HumanReview, never directly from Reviewing.
            [ProjectStatus.Reviewing] = [ProjectStatus.HumanReview, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.HumanReview] = [ProjectStatus.Completed, ProjectStatus.Implementation, ProjectStatus.Paused, ProjectStatus.Failed, ProjectStatus.Cancelled],
            [ProjectStatus.Paused] =
            [
                ProjectStatus.Analyzing, ProjectStatus.Specification, ProjectStatus.Architecture,
                ProjectStatus.Planning, ProjectStatus.Implementation, ProjectStatus.Testing,
                ProjectStatus.Repairing, ProjectStatus.Reviewing, ProjectStatus.HumanReview, ProjectStatus.Cancelled
            ],
            [ProjectStatus.Completed] = [],
            [ProjectStatus.Failed] = [],
            [ProjectStatus.Cancelled] = []
        };

    private static readonly IReadOnlyCollection<ProjectStatus> TerminalStatuses =
        [ProjectStatus.Completed, ProjectStatus.Failed, ProjectStatus.Cancelled];

    public static bool IsValid(ProjectStatus from, ProjectStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(ProjectStatus status) => TerminalStatuses.Contains(status);
}
