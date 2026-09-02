using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AuditLoggerTests
{
    private sealed class RecordingRepository : IAuditLogRepository
    {
        public AuditLogEntry? Recorded { get; private set; }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Recorded = entry;
            return Task.CompletedTask;
        }

        public Task<AuditLogPage> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditLogPage(Array.Empty<AuditLogEntry>(), 0, 1, 50));

        public Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<AuditLogEntry>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

        public Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

        public Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(int count, Guid? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());
    }

    private sealed class ThrowingRepository : IAuditLogRepository
    {
        public Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<AuditLogPage> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditLogPage(Array.Empty<AuditLogEntry>(), 0, 1, 50));

        public Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<AuditLogEntry>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

        public Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

        public Task<IReadOnlyList<AuditLogEntry>> GetRecentFailuresAsync(int count, Guid? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());
    }

    [Fact]
    public async Task LogAsync_PersistsEntry_WithAllFields()
    {
        var repository = new RecordingRepository();
        var auditLogger = new AuditLogger(repository, new AuditOptions(), NullLogger<AuditLogger>.Instance);
        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();

        await auditLogger.LogAsync(
            executionId, AuditEventType.AgentStarted, AuditSeverity.Info, "agent started",
            projectId: projectId, workItemId: workItemId);

        Assert.NotNull(repository.Recorded);
        Assert.Equal(executionId, repository.Recorded!.ExecutionId);
        Assert.Equal(projectId, repository.Recorded.ProjectId);
        Assert.Equal(workItemId, repository.Recorded.WorkItemId);
        Assert.Equal(AuditEventType.AgentStarted, repository.Recorded.EventType);
        Assert.Equal(AuditSeverity.Info, repository.Recorded.Severity);
        Assert.Equal("agent started", repository.Recorded.Message);
    }

    [Fact]
    public async Task LogAsync_StripsSecretShapedMetadataKeys_BeforePersisting()
    {
        var repository = new RecordingRepository();
        var auditLogger = new AuditLogger(repository, new AuditOptions(), NullLogger<AuditLogger>.Instance);

        await auditLogger.LogAsync(
            Guid.NewGuid(), AuditEventType.ModelSelected, AuditSeverity.Info, "model selected",
            metadata: new Dictionary<string, string>
            {
                ["apiKey"] = "sk-super-secret",
                ["authorizationToken"] = "bearer-xyz",
                ["model"] = "coding-optimized-model-a"
            });

        var json = repository.Recorded!.MetadataJson;
        Assert.NotNull(json);
        Assert.DoesNotContain("sk-super-secret", json);
        Assert.DoesNotContain("bearer-xyz", json);
        Assert.Contains("coding-optimized-model-a", json);
    }

    [Fact]
    public async Task LogAsync_SwallowsRepositoryFailure_ByDefault()
    {
        var auditLogger = new AuditLogger(
            new ThrowingRepository(), new AuditOptions { FailOnAuditError = false }, NullLogger<AuditLogger>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            auditLogger.LogAsync(Guid.NewGuid(), AuditEventType.WorkflowStarted, AuditSeverity.Info, "started"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LogAsync_PropagatesRepositoryFailure_WhenConfiguredCritical()
    {
        var auditLogger = new AuditLogger(
            new ThrowingRepository(), new AuditOptions { FailOnAuditError = true }, NullLogger<AuditLogger>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auditLogger.LogAsync(Guid.NewGuid(), AuditEventType.WorkflowStarted, AuditSeverity.Info, "started"));
    }
}
