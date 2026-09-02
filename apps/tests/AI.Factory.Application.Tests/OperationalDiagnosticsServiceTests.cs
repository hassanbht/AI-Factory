using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OperationalDiagnosticsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public OperationalDiagnosticsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private OperationalDiagnosticsService MakeService(FactoryDbContext db) => new(
        new ProjectRepository(db),
        new WorkItemRepository(db),
        new ApprovalRepository(db),
        new OrchestrationRunRepository(db),
        new AuditLogRepository(db));

    private async Task<Guid> SeedProjectWithTaskAsync(FactoryDbContext db, int attemptCount = 2)
    {
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = $"Diag Project {Guid.NewGuid():N}",
            Slug = $"diag-project-{Guid.NewGuid():N}",
            RootPath = string.Empty,
            Status = ProjectStatus.Implementation
        });

        db.WorkItems.Add(new WorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "Do the thing",
            Status = WorkItemStatus.Repairing.ToString(),
            AttemptCount = attemptCount,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return projectId;
    }

    [Fact]
    public async Task GetProjectSummaryAsync_ReturnsNull_WhenProjectDoesNotExist()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        var summary = await service.GetProjectSummaryAsync(Guid.NewGuid());

        Assert.Null(summary);
    }

    [Fact]
    public async Task GetProjectSummaryAsync_AggregatesLatestTaskAndAuditHistory()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedProjectWithTaskAsync(db, attemptCount: 3);

        db.AuditLogEntries.AddRange(
            new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ProjectId = projectId,
                EventType = AuditEventType.WorkflowFailed,
                Severity = AuditSeverity.Error,
                Message = "build failed",
                FailureCategory = FailureCategory.Build,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1)
            },
            new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ProjectId = projectId,
                EventType = AuditEventType.WorkflowCompleted,
                Severity = AuditSeverity.Info,
                Message = "completed",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-2)
            });
        await db.SaveChangesAsync();

        var service = MakeService(db);
        var summary = await service.GetProjectSummaryAsync(projectId);

        Assert.NotNull(summary);
        Assert.Equal(ProjectStatus.Implementation.ToString(), summary!.ProjectStatus);
        Assert.Equal(3, summary.RepairAttemptCount);
        Assert.NotNull(summary.LastFailureAtUtc);
        Assert.Equal(FailureCategory.Build, summary.LastFailureCategory);
        Assert.NotNull(summary.LastSuccessAtUtc);
    }

    [Fact]
    public async Task GetCorrelationSummaryAsync_ReturnsNull_WhenNothingRecordedForThatCorrelation()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        var summary = await service.GetCorrelationSummaryAsync(Guid.NewGuid());

        Assert.Null(summary);
    }

    [Fact]
    public async Task GetCorrelationSummaryAsync_FlagsHasFailures_WhenAnyEntryIsWarningOrAbove()
    {
        await using var db = new FactoryDbContext(_options);
        var correlationId = Guid.NewGuid();

        db.AuditLogEntries.AddRange(
            new AuditLogEntry
            {
                Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), CorrelationId = correlationId,
                EventType = AuditEventType.AgentStarted, Severity = AuditSeverity.Info, Message = "started",
                TimestampUtc = DateTime.UtcNow.AddSeconds(-2)
            },
            new AuditLogEntry
            {
                Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), CorrelationId = correlationId,
                EventType = AuditEventType.BuildResult, Severity = AuditSeverity.Warning, Message = "build failed",
                TimestampUtc = DateTime.UtcNow.AddSeconds(-1)
            });
        await db.SaveChangesAsync();

        var service = MakeService(db);
        var summary = await service.GetCorrelationSummaryAsync(correlationId);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.EntryCount);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public async Task GetRecentFailuresAsync_DelegatesToRepository_ExcludingInfoSeverity()
    {
        await using var db = new FactoryDbContext(_options);

        db.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(),
            EventType = AuditEventType.WorkflowFailed, Severity = AuditSeverity.Error, Message = "failed",
            TimestampUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = MakeService(db);
        var failures = await service.GetRecentFailuresAsync(10);

        Assert.Single(failures);
    }
}
