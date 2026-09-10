using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.3 §10/§17 — generic AgentRun -> WorkItem completion mapping.</summary>
public sealed class AgentRunWorkItemSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunWorkItemSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedRunningTaskAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Sync Project", "d");
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        return item.Id;
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded, WorkItemStatus.Completed)]
    [InlineData(AgentRunStatus.Failed, WorkItemStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled, WorkItemStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut, WorkItemStatus.Failed)]
    public async Task SyncAsync_TerminalAgentRun_TransitionsWorkItem(AgentRunStatus runStatus, WorkItemStatus expected)
    {
        var taskId = await SeedRunningTaskAsync();
        var run = new AgentRun { WorkItemId = taskId, Status = runStatus };

        await using var db = new FactoryDbContext(_options);
        await new AgentRunWorkItemSync(new WorkItemRepository(db), NullLogger<AgentRunWorkItemSync>.Instance)
            .SyncAsync(run);

        var workItem = await new WorkItemRepository(db).GetByIdAsync(taskId);
        Assert.Equal(expected.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task SyncAsync_AlreadyTerminalWorkItem_NotOverwritten()
    {
        var taskId = await SeedRunningTaskAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            await workItems.TransitionStatusAsync(taskId, WorkItemStatus.Completed);
        }

        var run = new AgentRun { WorkItemId = taskId, Status = AgentRunStatus.Failed };

        await using var syncDb = new FactoryDbContext(_options);
        await new AgentRunWorkItemSync(new WorkItemRepository(syncDb), NullLogger<AgentRunWorkItemSync>.Instance)
            .SyncAsync(run);

        var workItem = await new WorkItemRepository(syncDb).GetByIdAsync(taskId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task SyncAsync_NoWorkItemId_NoOp()
    {
        var run = new AgentRun { WorkItemId = null, Status = AgentRunStatus.Succeeded };

        await using var db = new FactoryDbContext(_options);
        await new AgentRunWorkItemSync(new WorkItemRepository(db), NullLogger<AgentRunWorkItemSync>.Instance)
            .SyncAsync(run);

        // No exception, no WorkItem touched — nothing further to assert.
    }
}
