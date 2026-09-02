using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IWorkflowTransitionValidator
{
    bool CanTransition(ProjectStatus from, ProjectStatus to);
}
