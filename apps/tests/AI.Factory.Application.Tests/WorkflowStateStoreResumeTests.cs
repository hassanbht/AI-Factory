using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class WorkflowStateStoreResumeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public WorkflowStateStoreResumeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task ApplyTransitionAsync_PersistsAcrossNewDbContextInstance()
    {
        var projectId = Guid.NewGuid();

        await using (var db = new FactoryDbContext(_options))
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Resume Test",
                Slug = "resume-test",
                Status = ProjectStatus.Created,
                RootPath = "projects/resume-test",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        await using (var writeDb = new FactoryDbContext(_options))
        {
            var store = new WorkflowStateStore(writeDb);

            var transition = new WorkflowTransition
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                FromStatus = ProjectStatus.Created,
                ToStatus = ProjectStatus.Analyzing,
                Reason = "restart-test",
                Actor = "tester",
                CreatedAtUtc = DateTime.UtcNow
            };

            await store.ApplyTransitionAsync(transition);
        }

        // A brand-new DbContext + store instance stands in for a process restart:
        // nothing here is shared in memory with the write above.
        await using (var readDb = new FactoryDbContext(_options))
        {
            var resumedStore = new WorkflowStateStore(readDb);

            var status = await resumedStore.GetCurrentStatusAsync(projectId);

            Assert.Equal(ProjectStatus.Analyzing, status);

            var persistedTransition = await readDb.WorkflowTransitions
                .SingleAsync(x => x.ProjectId == projectId);

            Assert.Equal(ProjectStatus.Created, persistedTransition.FromStatus);
            Assert.Equal(ProjectStatus.Analyzing, persistedTransition.ToStatus);
        }
    }
}
