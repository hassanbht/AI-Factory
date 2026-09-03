using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DatabaseTransactionManagerTests
{
    private static DbContextOptions<FactoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(connection)
            .Options;

    [Fact]
    public async Task ExecuteInTransactionAsync_CommitsAllChanges_WhenOperationSucceeds()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);

        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var metrics = new OperationalMetrics();

        await using var db = new FactoryDbContext(options);
        var txManager = new DatabaseTransactionManager(db, metrics, NullLogger<DatabaseTransactionManager>.Instance);

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();

        await txManager.ExecuteInTransactionAsync(async ct =>
        {
            var project = new Project
            {
                Id = projectId,
                Name = "Project in Tx",
                Slug = "tx-proj",
                RootPath = "/tx"
            };
            await db.Projects.AddAsync(project, ct);
            await db.SaveChangesAsync(ct);

            var workItem = new WorkItem
            {
                Id = workItemId,
                ProjectId = projectId,
                Title = "WorkItem in Tx",
                Type = Domain.Enums.WorkItemType.Implementation
            };
            await db.WorkItems.AddAsync(workItem, ct);
            await db.SaveChangesAsync(ct);
        });

        // Verify outside transaction that both were committed
        await using var dbVerify = new FactoryDbContext(options);
        Assert.NotNull(await dbVerify.Projects.FindAsync(projectId));
        Assert.NotNull(await dbVerify.WorkItems.FindAsync(workItemId));
        Assert.Equal(0, metrics.GetSnapshot().DatabaseTransactionFailures);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_RollsBackAllChanges_WhenFailureOccurs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);

        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var metrics = new OperationalMetrics();

        await using var db = new FactoryDbContext(options);
        var txManager = new DatabaseTransactionManager(db, metrics, NullLogger<DatabaseTransactionManager>.Instance);

        var projectId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            txManager.ExecuteInTransactionAsync(async ct =>
            {
                var project = new Project
                {
                    Id = projectId,
                    Name = "Project to Rollback",
                    Slug = "rollback-proj",
                    RootPath = "/rollback"
                };
                await db.Projects.AddAsync(project, ct);
                await db.SaveChangesAsync(ct);

                // Simulate failure on second step
                throw new InvalidOperationException("Simulated second step failure");
            }));

        // Verify outside transaction that project was rolled back and does not exist in DB
        await using var dbVerify = new FactoryDbContext(options);
        var rolledBackProject = await dbVerify.Projects.FindAsync(projectId);
        Assert.Null(rolledBackProject);

        Assert.Equal(1, metrics.GetSnapshot().DatabaseTransactionFailures);
    }
}
