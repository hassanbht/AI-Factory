using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class IdempotentPersistenceTests
{
    [Fact]
    public async Task AuditLogRepository_RecordAsync_IsIdempotent_OnRetryWithSameId()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        await using var db = new FactoryDbContext(options);
        var repo = new AuditLogRepository(db);

        var entryId = Guid.NewGuid();
        var entry = new AuditLogEntry
        {
            Id = entryId,
            EventType = AuditEventType.WorkflowStarted,
            Severity = AuditSeverity.Info,
            Message = "Starting workflow",
            TimestampUtc = DateTime.UtcNow
        };

        // First attempt succeeds
        await repo.RecordAsync(entry);

        // Second attempt with same ID (retry scenario) must be safe and idempotent
        await repo.RecordAsync(entry);

        // Verify exactly one record exists
        var count = await db.AuditLogEntries.CountAsync(x => x.Id == entryId);
        Assert.Equal(1, count);
    }
}
