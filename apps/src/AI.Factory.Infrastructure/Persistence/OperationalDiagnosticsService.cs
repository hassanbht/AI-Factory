using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class OperationalDiagnosticsService : IOperationalDiagnosticsService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly IApprovalRepository _approvalRepository;
    private readonly IOrchestrationRunRepository _orchestrationRunRepository;
    private readonly IAuditLogRepository _auditLogRepository;

    public OperationalDiagnosticsService(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        IApprovalRepository approvalRepository,
        IOrchestrationRunRepository orchestrationRunRepository,
        IAuditLogRepository auditLogRepository)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _approvalRepository = approvalRepository;
        _orchestrationRunRepository = orchestrationRunRepository;
        _auditLogRepository = auditLogRepository;
    }

    public async Task<ProjectDiagnosticSummary?> GetProjectSummaryAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _projectRepository.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        var workItems = await _workItemRepository.GetByProjectIdAsync(projectId, cancellationToken);
        var latestTask = workItems.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();

        OrchestrationRun? latestRun = latestTask is null
            ? null
            : await _orchestrationRunRepository.GetActiveByWorkItemIdAsync(latestTask.Id, cancellationToken);

        // Bounded, in-memory scan over the project's recent audit history —
        // deliberately not a full-history analytics query (Section 7: "do
        // not create a complex analytics engine").
        var recent = await _auditLogRepository.GetPagedAsync(
            new AuditLogQuery(ProjectId: projectId, Page: 1, PageSize: 200), cancellationToken);

        var lastSuccess = recent.Items
            .Where(x => x.EventType == AuditEventType.WorkflowCompleted)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefault();

        var lastFailure = recent.Items
            .Where(x => x.EventType == AuditEventType.WorkflowFailed)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefault();

        var latestApproval = await _approvalRepository.GetLatestForProjectAsync(projectId, cancellationToken);

        return new ProjectDiagnosticSummary(
            ProjectId: projectId,
            ProjectStatus: project.Status.ToString(),
            LatestTaskId: latestTask?.Id,
            LatestTaskStatus: latestTask?.Status,
            RepairAttemptCount: latestTask?.AttemptCount ?? 0,
            LatestRunId: latestRun?.Id,
            LatestRunStatus: latestRun?.Status.ToString(),
            LastSuccessAtUtc: lastSuccess?.TimestampUtc,
            LastFailureAtUtc: lastFailure?.TimestampUtc,
            LastFailureCategory: lastFailure?.FailureCategory,
            LastFailureMessage: lastFailure?.Message,
            LatestApprovalDecision: latestApproval?.Decision);
    }

    public async Task<CorrelationSummary?> GetCorrelationSummaryAsync(
        Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entries = await _auditLogRepository.GetByCorrelationIdAsync(correlationId, cancellationToken);

        if (entries.Count == 0)
        {
            return null;
        }

        return new CorrelationSummary(
            CorrelationId: correlationId,
            EntryCount: entries.Count,
            FirstEntryAtUtc: entries[0].TimestampUtc,
            LastEntryAtUtc: entries[^1].TimestampUtc,
            HasFailures: entries.Any(x => x.Severity >= AuditSeverity.Warning),
            Entries: entries);
    }

    public Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(
        int count, Guid? projectId = null, CancellationToken cancellationToken = default)
        => _auditLogRepository.GetRecentFailuresAsync(count, projectId, cancellationToken);

    public Task<IReadOnlyList<AuditLogEntry>> GetRecentOperationsAsync(
        int count, CancellationToken cancellationToken = default)
        => _auditLogRepository.GetRecentAsync(count, cancellationToken);
}
