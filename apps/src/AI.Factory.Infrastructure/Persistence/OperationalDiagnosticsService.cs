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
    private readonly IAgentRunRepository? _agentRunRepository;

    public OperationalDiagnosticsService(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        IApprovalRepository approvalRepository,
        IOrchestrationRunRepository orchestrationRunRepository,
        IAuditLogRepository auditLogRepository,
        IAgentRunRepository? agentRunRepository = null)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _approvalRepository = approvalRepository;
        _orchestrationRunRepository = orchestrationRunRepository;
        _auditLogRepository = auditLogRepository;
        _agentRunRepository = agentRunRepository;
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

        var statusCounts = workItems
            .GroupBy(w => w.Status ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var activeAgentRuns = 0;
        var staleAgentRuns = 0;
        long? executionDurationMs = null;

        if (_agentRunRepository is not null)
        {
            var runsPage = await _agentRunRepository.GetPagedAsync(
                new AgentRunQuery(ProjectId: projectId, Page: 1, PageSize: 100), cancellationToken);

            activeAgentRuns = runsPage.Items.Count(r => r.Status == AgentRunStatus.Running);
            var staleCutoffUtc = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            staleAgentRuns = runsPage.Items.Count(r => r.Status == AgentRunStatus.Running && r.UpdatedAtUtc < staleCutoffUtc);

            var latestAgentRun = runsPage.Items.OrderByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            if (latestAgentRun?.CompletedAtUtc is not null && latestAgentRun.StartedAtUtc is not null)
            {
                executionDurationMs = (long)(latestAgentRun.CompletedAtUtc.Value - latestAgentRun.StartedAtUtc.Value).TotalMilliseconds;
            }
        }

        var attemptCount = latestTask?.AttemptCount ?? 0;
        var repairAttemptCount = latestTask?.RepairAttemptCount ?? 0;
        if (repairAttemptCount == 0 && latestTask is not null && latestTask.Status == WorkItemStatus.Repairing.ToString())
        {
            repairAttemptCount = latestTask.AttemptCount;
        }

        var sanitizedErrorMessage = lastFailure?.Message is not null
            ? AI.Factory.Application.Security.SensitiveDataSanitizer.Sanitize(lastFailure.Message)
            : null;

        return new ProjectDiagnosticSummary(
            ProjectId: projectId,
            ProjectStatus: project.Status.ToString(),
            LatestTaskId: latestTask?.Id,
            LatestTaskStatus: latestTask?.Status,
            RepairAttemptCount: repairAttemptCount,
            LatestRunId: latestRun?.Id,
            LatestRunStatus: latestRun?.Status.ToString(),
            LastSuccessAtUtc: lastSuccess?.TimestampUtc,
            LastFailureAtUtc: lastFailure?.TimestampUtc,
            LastFailureCategory: lastFailure?.FailureCategory,
            LastFailureMessage: sanitizedErrorMessage,
            LatestApprovalDecision: latestApproval?.Decision,
            WorkItemStatusCounts: statusCounts,
            ActiveAgentRuns: activeAgentRuns,
            StaleAgentRuns: staleAgentRuns,
            ExecutionDurationMs: executionDurationMs,
            AttemptCount: attemptCount);
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
