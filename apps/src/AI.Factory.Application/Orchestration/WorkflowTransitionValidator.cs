using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class WorkflowTransitionValidator : IWorkflowTransitionValidator
{
    private static readonly ProjectStatus[] LinearSequence =
    {
        ProjectStatus.Created,
        ProjectStatus.Analyzing,
        ProjectStatus.Specification,
        ProjectStatus.Architecture,
        ProjectStatus.Planning,
        ProjectStatus.Implementation,
        ProjectStatus.Testing,
        ProjectStatus.Repairing,
        ProjectStatus.Reviewing,
        ProjectStatus.HumanReview,
        ProjectStatus.Completed
    };

    private static readonly HashSet<ProjectStatus> TerminalStatuses = new()
    {
        ProjectStatus.Completed,
        ProjectStatus.Failed,
        ProjectStatus.Cancelled
    };

    public bool CanTransition(ProjectStatus from, ProjectStatus to)
    {
        if (TerminalStatuses.Contains(from))
        {
            return false;
        }

        if (to == ProjectStatus.Failed || to == ProjectStatus.Cancelled || to == ProjectStatus.HumanReview)
        {
            return true;
        }

        var fromIndex = Array.IndexOf(LinearSequence, from);
        var toIndex = Array.IndexOf(LinearSequence, to);

        return fromIndex >= 0 && toIndex == fromIndex + 1;
    }
}
