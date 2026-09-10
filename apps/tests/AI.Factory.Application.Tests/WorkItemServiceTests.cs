using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §15 — Task (WorkItem) creation/ownership/retrieval/lifecycle.</summary>
public sealed class WorkItemServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public WorkItemServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static WorkItemService CreateService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new ProjectRepository(db));

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Task Project", "d");
        return project.Id;
    }

    [Fact]
    public async Task CreateAsync_ValidProject_PersistsAsDraft_OwnedByProject()
    {
        var projectId = await SeedProjectAsync();

        await using var db = new FactoryDbContext(_options);
        var task = await CreateService(db).CreateAsync(
            projectId, WorkItemType.Implementation, "Do the thing", "desc", "Normal");

        Assert.Equal(projectId, task.ProjectId);
        Assert.Equal(WorkItemStatus.Draft.ToString(), task.Status);
    }

    [Fact]
    public async Task CreateAsync_UnknownProject_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService(db).CreateAsync(Guid.NewGuid(), WorkItemType.Implementation, "x", null, "Normal"));
    }

    [Fact]
    public async Task GetAsync_And_ListByProjectAsync_ReturnCreatedTask()
    {
        var projectId = await SeedProjectAsync();
        Guid taskId;
        await using (var db = new FactoryDbContext(_options))
        {
            taskId = (await CreateService(db).CreateAsync(projectId, WorkItemType.Test, "T", null, "Low")).Id;
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var service = CreateService(verifyDb);
        Assert.NotNull(await service.GetAsync(taskId));
        Assert.Single(await service.ListByProjectAsync(projectId));
    }

    [Fact]
    public async Task TransitionStatusAsync_ValidTransition_Persists()
    {
        var projectId = await SeedProjectAsync();
        Guid taskId;
        await using (var db = new FactoryDbContext(_options))
        {
            taskId = (await CreateService(db).CreateAsync(projectId, WorkItemType.Implementation, "T", null, "Normal")).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).TransitionStatusAsync(taskId, WorkItemStatus.Ready);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await CreateService(verifyDb).GetAsync(taskId);
        Assert.Equal(WorkItemStatus.Ready.ToString(), reloaded!.Status);
    }

    [Fact]
    public async Task TransitionStatusAsync_InvalidTransition_Throws()
    {
        var projectId = await SeedProjectAsync();
        Guid taskId;
        await using (var db = new FactoryDbContext(_options))
        {
            taskId = (await CreateService(db).CreateAsync(projectId, WorkItemType.Implementation, "T", null, "Normal")).Id;
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<InvalidWorkItemTransitionException>(
            () => CreateService(db2).TransitionStatusAsync(taskId, WorkItemStatus.Running));
    }
}
