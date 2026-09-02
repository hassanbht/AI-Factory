using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Application.Approvals;

/// <summary>
/// The single authoritative way to pause a project's workflow for human
/// approval — persists an ApprovalRequest and transitions the workflow to
/// HumanReview together, so the two never drift apart. The workflow never
/// continues automatically; only ApproveAsync/RejectAsync move it forward.
/// </summary>
public sealed class ApprovalService
{
    private readonly IApprovalRepository _repository;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IAuditLogger _auditLogger;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<ApprovalService> _logger;

    public ApprovalService(
        IApprovalRepository repository,
        IWorkflowEngine workflowEngine,
        IAuditLogger? auditLogger = null,
        IOperationalMetrics? metrics = null,
        ILogger<ApprovalService>? logger = null)
    {
        _repository = repository;
        _workflowEngine = workflowEngine;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        _metrics = metrics ?? new OperationalMetrics();
        _logger = logger ?? NullLogger<ApprovalService>.Instance;
    }

    public async Task<ApprovalRequest> RequestApprovalAsync(
        Guid projectId,
        string reason,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        var transition = await _workflowEngine.TransitionAsync(
            new WorkflowTransitionRequest(projectId, ProjectStatus.HumanReview, reason, requestedBy),
            cancellationToken);

        var request = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Workflow = transition.FromStatus,
            Reason = reason,
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = requestedBy,
            Decision = ApprovalDecision.PendingApproval
        };

        var created = await _repository.CreateAsync(request, cancellationToken);

        _logger.LogInformation(
            "Approval requested for project {ProjectId}: {Reason} (requested by {RequestedBy})",
            projectId, reason, requestedBy);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.HumanApproval,
            AuditSeverity.Info,
            $"Approval requested: {reason}",
            projectId: projectId,
            cancellationToken: cancellationToken);

        return created;
    }

    public async Task<ApprovalRequest> ApproveAsync(
        Guid approvalId,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var approval = await GetOrThrow(approvalId, cancellationToken);

        await _repository.DecideAsync(approvalId, ApprovalDecision.Approved, comment, cancellationToken);

        await _workflowEngine.TransitionAsync(
            new WorkflowTransitionRequest(
                approval.ProjectId, ProjectStatus.Completed, comment ?? "Approved.", "human"),
            cancellationToken);

        _logger.LogInformation(
            "Approval {ApprovalId} for project {ProjectId} was Approved",
            approvalId, approval.ProjectId);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.HumanApproval,
            AuditSeverity.Info,
            $"Approval '{approvalId}' was Approved: {comment}",
            projectId: approval.ProjectId,
            outcome: "Approved",
            actorType: "Human",
            cancellationToken: cancellationToken);

        _metrics.RecordApproval(approved: true);

        return (await _repository.GetByIdAsync(approvalId, cancellationToken))!;
    }

    public async Task<ApprovalRequest> RejectAsync(
        Guid approvalId,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var approval = await GetOrThrow(approvalId, cancellationToken);

        await _repository.DecideAsync(approvalId, ApprovalDecision.Rejected, comment, cancellationToken);

        await _workflowEngine.TransitionAsync(
            new WorkflowTransitionRequest(
                approval.ProjectId, ProjectStatus.Failed, comment ?? "Rejected.", "human"),
            cancellationToken);

        _logger.LogWarning(
            "Approval {ApprovalId} for project {ProjectId} was Rejected [{FailureCategory}]: {Comment}",
            approvalId, approval.ProjectId, PipelineFailureCategory.HumanRejection, comment);

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.HumanApproval,
            AuditSeverity.Warning,
            $"Approval '{approvalId}' was Rejected: {comment}",
            projectId: approval.ProjectId,
            outcome: "Rejected",
            actorType: "Human",
            cancellationToken: cancellationToken);

        _metrics.RecordApproval(approved: false);

        return (await _repository.GetByIdAsync(approvalId, cancellationToken))!;
    }

    public Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.GetPendingAsync(cancellationToken);
    }

    private async Task<ApprovalRequest> GetOrThrow(Guid approvalId, CancellationToken cancellationToken)
    {
        return await _repository.GetByIdAsync(approvalId, cancellationToken)
            ?? throw new NotFoundException($"ApprovalRequest '{approvalId}' was not found.");
    }
}
