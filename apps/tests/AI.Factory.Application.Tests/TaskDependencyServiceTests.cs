using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §7/§15 — Task dependency add/remove validation and cycle rejection.</summary>
public sealed class TaskDependencyServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public TaskDependencyServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static TaskDependencyService CreateService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new TaskDependencyResolver());

    private static WorkItemService CreateWorkItemService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new ProjectRepository(db));

    private async Task<(Guid ProjectId, Guid TaskA, Guid TaskB)> SeedTwoTasksAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Dep Project", "d");
        var workItems = CreateWorkItemService(db);
        var a = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "A", null, "Normal");
        var b = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "B", null, "Normal");
        return (project.Id, a.Id, b.Id);
    }

    [Fact]
    public async Task AddDependencyAsync_Valid_Persists()
    {
        var (_, taskA, taskB) = await SeedTwoTasksAsync();

        await using var db = new FactoryDbContext(_options);
        await CreateService(db).AddDependencyAsync(taskA, taskB);

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.True(await new WorkItemRepository(verifyDb).DependencyExistsAsync(taskA, taskB));
    }

    [Fact]
    public async Task AddDependencyAsync_SelfDependency_Throws()
    {
        var (_, taskA, _) = await SeedTwoTasksAsync();

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).AddDependencyAsync(taskA, taskA));
    }

    [Fact]
    public async Task AddDependencyAsync_Duplicate_Throws()
    {
        var (_, taskA, taskB) = await SeedTwoTasksAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).AddDependencyAsync(taskA, taskB);
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db2).AddDependencyAsync(taskA, taskB));
    }

    [Fact]
    public async Task AddDependencyAsync_CrossProject_Throws()
    {
        await using var db = new FactoryDbContext(_options);
        var projects = new ProjectService(new ProjectRepository(db));
        var p1 = await projects.CreateAsync("P1", "d");
        var p2 = await projects.CreateAsync("P2", "d");
        var workItems = CreateWorkItemService(db);
        var taskA = await workItems.CreateAsync(p1.Id, WorkItemType.Implementation, "A", null, "Normal");
        var taskB = await workItems.CreateAsync(p2.Id, WorkItemType.Implementation, "B", null, "Normal");

        await Assert.ThrowsAsync<ValidationException>(
            () => CreateService(db).AddDependencyAsync(taskA.Id, taskB.Id));
    }

    [Fact]
    public async Task AddDependencyAsync_CircularDependency_Throws()
    {
        var (_, taskA, taskB) = await SeedTwoTasksAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).AddDependencyAsync(taskA, taskB);
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db2).AddDependencyAsync(taskB, taskA));
    }

    [Fact]
    public async Task RemoveDependencyAsync_Existing_Succeeds()
    {
        var (_, taskA, taskB) = await SeedTwoTasksAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).AddDependencyAsync(taskA, taskB);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).RemoveDependencyAsync(taskA, taskB);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.False(await new WorkItemRepository(verifyDb).DependencyExistsAsync(taskA, taskB));
    }

    [Fact]
    public async Task RemoveDependencyAsync_Missing_ThrowsNotFound()
    {
        var (_, taskA, taskB) = await SeedTwoTasksAsync();

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).RemoveDependencyAsync(taskA, taskB));
    }
}
