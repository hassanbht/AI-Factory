using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class AuditLogger : IAuditLogger
{
    private static readonly string[] SecretShapedKeyFragments =
    {
        "key", "token", "secret", "password", "credential", "authorization"
    };

    private readonly IAuditLogRepository _repository;
    private readonly AuditOptions _options;
    private readonly IOperationContextAccessor _operationContextAccessor;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(
        IAuditLogRepository repository,
        AuditOptions options,
        ILogger<AuditLogger> logger,
        IOperationContextAccessor? operationContextAccessor = null,
        IOperationalMetrics? metrics = null)
    {
        _repository = repository;
        _options = options;
        _logger = logger;
        _operationContextAccessor = operationContextAccessor ?? new OperationContextAccessor();
        _metrics = metrics ?? new OperationalMetrics();
    }

    public async Task LogAsync(
        Guid executionId,
        AuditEventType eventType,
        AuditSeverity severity,
        string message,
        Guid? projectId = null,
        Guid? workItemId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        Guid? correlationId = null,
        Guid? operationId = null,
        Guid? runId = null,
        string? outcome = null,
        string? actorType = null,
        string? actorId = null,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        var ambient = _operationContextAccessor.Current;

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            ProjectId = projectId ?? ambient?.ProjectId,
            WorkItemId = workItemId ?? ambient?.TaskId,
            TimestampUtc = DateTime.UtcNow,
            EventType = eventType,
            Severity = severity,
            Message = AI.Factory.Application.Security.SensitiveDataSanitizer.Sanitize(message),
            MetadataJson = SerializeMetadata(metadata),
            CorrelationId = correlationId ?? ambient?.CorrelationId ?? executionId,
            OperationId = operationId ?? ambient?.OperationId,
            ParentOperationId = ambient?.ParentOperationId,
            RunId = runId ?? ambient?.RunId,
            Outcome = outcome,
            ActorType = actorType ?? "System",
            ActorId = actorId
        };

        try
        {
            await _repository.RecordAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (!_options.FailOnAuditError)
        {
            // Audit is observability, not a workflow dependency — losing one
            // entry must never take down the pipeline it is describing,
            // unless an operator has explicitly opted into the opposite.
            _logger.LogWarning(
                ex, "Failed to persist audit entry: ExecutionId={ExecutionId}, EventType={EventType}", executionId, eventType);
        }

        RecordMetrics(eventType, severity, durationMs);
    }

    /// <summary>
    /// Every existing call site already reports (EventType, Severity) here —
    /// deriving the fixed Section 8 counters from that single funnel point
    /// avoids touching most existing LogAsync call sites individually.
    /// HumanApproval is deliberately excluded: severity alone can't tell
    /// "approval requested" apart from "approved" (both Info) — ApprovalService
    /// calls IOperationalMetrics.RecordApproval explicitly instead, where the
    /// outcome is unambiguous.
    /// </summary>
    private void RecordMetrics(AuditEventType eventType, AuditSeverity severity, long? durationMs)
    {
        var success = severity is AuditSeverity.Info;

        switch (eventType)
        {
            case AuditEventType.AgentCompleted:
                _metrics.RecordAgentExecution(success, durationMs);
                break;
            case AuditEventType.BuildResult:
                _metrics.RecordBuildExecution(success, durationMs);
                break;
            case AuditEventType.TestResult:
                _metrics.RecordTestExecution(success, durationMs);
                break;
            case AuditEventType.RepairAttempt:
                _metrics.RecordRepairAttempt(success, durationMs);
                break;
            case AuditEventType.RateLimitRejected:
                _metrics.RecordRateLimitRejected();
                break;
            case AuditEventType.SecurityRequestRejected:
                _metrics.RecordSecurityRequestRejected();
                break;
            case AuditEventType.OperationalAccessDenied:
                _metrics.RecordOperationalAccessDenied();
                break;
            case AuditEventType.ConfigurationValidationFailed:
                _metrics.RecordConfigurationValidationFailed();
                break;
            case AuditEventType.SecretResolutionFailed:
                _metrics.RecordSecretResolutionFailed();
                break;
            case AuditEventType.RequiredSecretMissing:
                _metrics.RecordRequiredSecretMissing();
                break;
            case AuditEventType.DatabaseRetry:
                _metrics.RecordDatabaseRetry();
                break;
            case AuditEventType.DatabaseConcurrencyConflict:
                _metrics.RecordDatabaseConcurrencyConflict();
                break;
            case AuditEventType.DatabaseTransactionFailed:
                _metrics.RecordDatabaseTransactionFailure();
                break;
            case AuditEventType.DatabaseOperationFailed:
                _metrics.RecordDatabaseOperation(false, durationMs);
                break;
        }
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var sanitized = AI.Factory.Application.Security.SensitiveDataSanitizer.SanitizeMetadata(metadata);
        return sanitized.Count == 0 ? null : JsonSerializer.Serialize(sanitized);
    }
}
