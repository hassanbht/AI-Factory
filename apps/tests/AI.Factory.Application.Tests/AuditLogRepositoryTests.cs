using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AuditLogRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AuditLogRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static AuditLogEntry MakeEntry(
        Guid? projectId = null,
        Guid? executionId = null,
        DateTime? timestampUtc = null,
        Guid? correlationId = null,
        AuditSeverity severity = AuditSeverity.Info) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionId = executionId ?? Guid.NewGuid(),
        ProjectId = projectId,
        TimestampUtc = timestampUtc ?? DateTime.UtcNow,
        EventType = AuditEventType.WorkflowStarted,
        Severity = severity,
        Message = "test entry",
        CorrelationId = correlationId
    };

    [Fact]
    public async Task GetPagedAsync_ReturnsEntries_NewestFirst_WithCorrectTotalCount()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);

        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddMinutes(-2)));
        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddMinutes(-1)));
        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow));

        var page = await repository.GetPagedAsync(new AuditLogQuery(Page: 1, PageSize: 2));

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].TimestampUtc >= page.Items[1].TimestampUtc);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByProjectId()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);
        var projectId = Guid.NewGuid();

        await repository.RecordAsync(MakeEntry(projectId: projectId));
        await repository.RecordAsync(MakeEntry(projectId: Guid.NewGuid()));

        var page = await repository.GetPagedAsync(new AuditLogQuery(ProjectId: projectId));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(projectId, page.Items[0].ProjectId);
    }

    [Fact]
    public async Task PurgeOlderThanAsync_DeletesOnlyExpiredEntries()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);

        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddDays(-100)));
        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddDays(-1)));

        var deleted = await repository.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-90));

        Assert.Equal(1, deleted);

        var remaining = await repository.GetPagedAsync(new AuditLogQuery());
        Assert.Equal(1, remaining.TotalCount);
    }

    [Fact]
    public async Task GetByCorrelationIdAsync_ReturnsOnlyMatchingEntries_OldestFirst()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);
        var correlationId = Guid.NewGuid();

        await repository.RecordAsync(MakeEntry(correlationId: correlationId, timestampUtc: DateTime.UtcNow.AddMinutes(-1)));
        await repository.RecordAsync(MakeEntry(correlationId: correlationId, timestampUtc: DateTime.UtcNow));
        await repository.RecordAsync(MakeEntry(correlationId: Guid.NewGuid()));

        var entries = await repository.GetByCorrelationIdAsync(correlationId);

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].TimestampUtc <= entries[1].TimestampUtc);
        Assert.All(entries, e => Assert.Equal(correlationId, e.CorrelationId));
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirst_BoundedByCount()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);

        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddMinutes(-2)));
        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow.AddMinutes(-1)));
        await repository.RecordAsync(MakeEntry(timestampUtc: DateTime.UtcNow));

        var entries = await repository.GetRecentAsync(2);

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].TimestampUtc >= entries[1].TimestampUtc);
    }

    [Fact]
    public async Task GetRecentFailuresAsync_ExcludesInfoSeverity_AndCanScopeToOneProject()
    {
        await using var db = new FactoryDbContext(_options);
        var repository = new AuditLogRepository(db);
        var projectId = Guid.NewGuid();

        await repository.RecordAsync(MakeEntry(projectId: projectId, severity: AuditSeverity.Info));
        await repository.RecordAsync(MakeEntry(projectId: projectId, severity: AuditSeverity.Warning));
        await repository.RecordAsync(MakeEntry(projectId: Guid.NewGuid(), severity: AuditSeverity.Error));

        var scoped = await repository.GetRecentFailuresAsync(10, projectId);
        Assert.Single(scoped);
        Assert.Equal(AuditSeverity.Warning, scoped[0].Severity);

        var all = await repository.GetRecentFailuresAsync(10);
        Assert.Equal(2, all.Count);
    }
}
