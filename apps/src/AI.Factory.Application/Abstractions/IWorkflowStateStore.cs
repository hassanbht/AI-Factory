using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IWorkflowStateStore
{
    Task<ProjectStatus> GetCurrentStatusAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task ApplyTransitionAsync(
        WorkflowTransition transition,
        CancellationToken cancellationToken = default);
}
