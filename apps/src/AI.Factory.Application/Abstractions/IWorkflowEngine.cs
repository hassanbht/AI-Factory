using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IWorkflowEngine
{
    Task<WorkflowTransition> TransitionAsync(
        WorkflowTransitionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowTransitionRequest(
    Guid ProjectId,
    ProjectStatus ToStatus,
    string? Reason,
    string? Actor);
