using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly IWorkflowStateStore _store;
    private readonly IWorkflowTransitionValidator _validator;
    private readonly IAuditLogger _auditLogger;

    public WorkflowEngine(
        IWorkflowStateStore store,
        IWorkflowTransitionValidator validator,
        IAuditLogger? auditLogger = null)
    {
        _store = store;
        _validator = validator;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
    }

    public async Task<WorkflowTransition> TransitionAsync(
        WorkflowTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentStatus = await _store.GetCurrentStatusAsync(
            request.ProjectId,
            cancellationToken);

        if (!_validator.CanTransition(currentStatus, request.ToStatus))
        {
            throw new ConflictException(
                $"Invalid workflow transition from '{currentStatus}' to '{request.ToStatus}' for project '{request.ProjectId}'.");
        }

        var transition = new WorkflowTransition
        {
            Id = Guid.NewGuid(),
            ProjectId = request.ProjectId,
            FromStatus = currentStatus,
            ToStatus = request.ToStatus,
            Reason = request.Reason,
            Actor = request.Actor,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _store.ApplyTransitionAsync(transition, cancellationToken);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkflowStateChanged,
            AuditSeverity.Info,
            $"Project '{request.ProjectId}' transitioned {currentStatus} -> {request.ToStatus}: {request.Reason}",
            projectId: request.ProjectId,
            cancellationToken: cancellationToken);

        return transition;
    }
}
