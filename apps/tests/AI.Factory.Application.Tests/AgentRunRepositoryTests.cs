using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AgentRunRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunRepositoryTests()
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

    private async Task<AgentDefinition> SeedAgentDefinitionAsync()
    {
        var definition = new AgentDefinition
        {
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = "Coding",
            SystemPrompt = "You are a coding agent."
        };

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentDefinitionRepository(db);
        return await repo.CreateAsync(definition);
    }

    [Fact]
    public async Task CreateAsync_PersistsAgentDefinition_ReloadableByRepository()
    {
        var created = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentDefinitionRepository(db);

        var reloaded = await repo.GetByIdAsync(created.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("Coder", reloaded!.Name);
        Assert.True(reloaded.Enabled);
    }

    [Fact]
    public async Task UpdateAsync_DisablesAgentDefinition()
    {
        var created = await SeedAgentDefinitionAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentDefinitionRepository(db);
            var definition = await repo.GetByIdAsync(created.Id);
            definition!.Enabled = false;
            await repo.UpdateAsync(definition);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentDefinitionRepository(db);
            var reloaded = await repo.GetByIdAsync(created.Id);
            Assert.False(reloaded!.Enabled);
        }
    }

    [Fact]
    public async Task CreateAsync_PersistsAgentRun_WithProjectAndWorkItemReferences()
    {
        var definition = await SeedAgentDefinitionAsync();
        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var created = await repo.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id,
            ProjectId = projectId,
            WorkItemId = workItemId,
            CorrelationId = correlationId
        });

        var reloaded = await repo.GetByIdAsync(created.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(definition.Id, reloaded!.AgentDefinitionId);
        Assert.Equal(projectId, reloaded.ProjectId);
        Assert.Equal(workItemId, reloaded.WorkItemId);
        Assert.Equal(AgentRunStatus.Created, reloaded.Status);
    }

    [Fact]
    public async Task CreateAsync_CalledTwiceWithSameCorrelationId_ReturnsSameAgentRun()
    {
        var definition = await SeedAgentDefinitionAsync();
        var correlationId = Guid.NewGuid();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var first = await repo.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id,
            CorrelationId = correlationId
        });

        var second = await repo.CreateAsync(new AgentRun
        {
            AgentDefinitionId = definition.Id,
            CorrelationId = correlationId
        });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.AgentRuns.CountAsync(x => x.CorrelationId == correlationId));
    }

    [Fact]
    public async Task UpdateAsync_PersistsStatusTransition_AcrossReload()
    {
        var definition = await SeedAgentDefinitionAsync();
        AgentRun created;

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            created = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            var run = await repo.GetByIdAsync(created.Id);
            run!.TransitionTo(AgentRunStatus.Queued);
            run.TransitionTo(AgentRunStatus.Running);
            await repo.UpdateAsync(run);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            var reloaded = await repo.GetByIdAsync(created.Id);
            Assert.Equal(AgentRunStatus.Running, reloaded!.Status);
            Assert.NotNull(reloaded.StartedAtUtc);
        }
    }

    [Fact]
    public async Task TryClaimNextPendingAsync_ReturnsNull_WhenNoQueuedRuns()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);
        await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });

        var claimed = await repo.TryClaimNextPendingAsync();

        Assert.Null(claimed);
    }

    [Fact]
    public async Task TryClaimNextPendingAsync_ClaimsOldestQueuedRun_AndTransitionsToRunning()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var older = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        older.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(older);

        var newer = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        newer.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(newer);

        var claimed = await repo.TryClaimNextPendingAsync();

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed!.Id);
        Assert.Equal(AgentRunStatus.Running, claimed.Status);
        Assert.NotNull(claimed.AgentDefinition);

        var reloaded = await repo.GetByIdAsync(older.Id);
        Assert.Equal(AgentRunStatus.Running, reloaded!.Status);
    }

    [Fact]
    public async Task TryClaimNextPendingAsync_SecondConcurrentCall_DoesNotClaimTheSameRowTwice()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            var run = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
            run.TransitionTo(AgentRunStatus.Queued);
            await repo.UpdateAsync(run);
        }

        await using var db1 = new FactoryDbContext(_options);
        await using var db2 = new FactoryDbContext(_options);
        var repo1 = new AgentRunRepository(db1);
        var repo2 = new AgentRunRepository(db2);

        var firstClaim = await repo1.TryClaimNextPendingAsync();
        var secondClaim = await repo2.TryClaimNextPendingAsync();

        Assert.NotNull(firstClaim);
        Assert.Null(secondClaim);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByStatus_AndOrdersNewestFirst()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var older = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        older.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(older);

        var newer = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        newer.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(newer);

        var page = await repo.GetPagedAsync(new AgentRunQuery(Status: AgentRunStatus.Queued));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(newer.Id, page.Items[0].Id);
        Assert.Equal(older.Id, page.Items[1].Id);
    }

    [Fact]
    public async Task GetPagedAsync_RespectsPageAndPageSize()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        for (var i = 0; i < 5; i++)
        {
            await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        }

        var page = await repo.GetPagedAsync(new AgentRunQuery(AgentDefinitionId: definition.Id, Page: 2, PageSize: 2));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
    }

    [Fact]
    public async Task CountByStatusAsync_ReturnsOnlyMatchingRuns()
    {
        var definition = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);

        var queued = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
        queued.TransitionTo(AgentRunStatus.Queued);
        await repo.UpdateAsync(queued);

        await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });

        Assert.Equal(1, await repo.CountByStatusAsync(AgentRunStatus.Queued));
        Assert.Equal(1, await repo.CountByStatusAsync(AgentRunStatus.Created));
        Assert.Equal(0, await repo.CountByStatusAsync(AgentRunStatus.Running));
    }
}
