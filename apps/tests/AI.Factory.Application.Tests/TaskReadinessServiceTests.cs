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

/// <summary>Phase 18.2 §11/§15 — Deterministic task readiness classification.</summary>
public sealed class TaskReadinessServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public TaskReadinessServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static TaskReadinessService CreateReadinessService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new TaskDependencyResolver());

    private static TaskDependencyService CreateDependencyService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new TaskDependencyResolver());

    private static WorkItemService CreateWorkItemService(FactoryDbContext db) =>
        new(new WorkItemRepository(db), new ProjectRepository(db));

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync("Readiness Project", "d");
        // Phase 18.16 §12 — WorkItem creation now requires a source decision
        // (Status != Created); this file is not exercising source init.
        project.Status = ProjectStatus.Analyzing;
        await repo.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task GetReadinessAsync_NoDependencies_ReturnsReady()
    {
        var projectId = await SeedProjectAsync();
        Guid taskId;
        await using (var db = new FactoryDbContext(_options))
        {
            taskId = (await CreateWorkItemService(db).CreateAsync(
                projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(TaskReadinessStatus.Ready, await CreateReadinessService(verifyDb).GetReadinessAsync(taskId));
    }

    [Fact]
    public async Task GetReadinessAsync_AllDependenciesCompleted_ReturnsReady()
    {
        var projectId = await SeedProjectAsync();
        Guid taskA, taskB;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            taskA = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
            taskB = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "B", null, "Normal")).Id;
            await CreateDependencyService(db).AddDependencyAsync(taskA, taskB);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Ready);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Running);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Completed);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(TaskReadinessStatus.Ready, await CreateReadinessService(verifyDb).GetReadinessAsync(taskA));
    }

    [Fact]
    public async Task GetReadinessAsync_PendingDependency_ReturnsWaitingForDependency()
    {
        var projectId = await SeedProjectAsync();
        Guid taskA;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            taskA = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
            var taskB = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "B", null, "Normal")).Id;
            await CreateDependencyService(db).AddDependencyAsync(taskA, taskB);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(
            TaskReadinessStatus.WaitingForDependency,
            await CreateReadinessService(verifyDb).GetReadinessAsync(taskA));
    }

    [Fact]
    public async Task GetReadinessAsync_FailedDependency_ReturnsBlockedByFailedDependency()
    {
        var projectId = await SeedProjectAsync();
        Guid taskA, taskB;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            taskA = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
            taskB = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "B", null, "Normal")).Id;
            await CreateDependencyService(db).AddDependencyAsync(taskA, taskB);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Ready);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Running);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Failed);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(
            TaskReadinessStatus.BlockedByFailedDependency,
            await CreateReadinessService(verifyDb).GetReadinessAsync(taskA));
    }

    [Fact]
    public async Task GetReadinessAsync_RunningDependency_ReturnsWaitingForDependency()
    {
        var projectId = await SeedProjectAsync();
        Guid taskA, taskB;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            taskA = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
            taskB = (await workItems.CreateAsync(projectId, WorkItemType.Implementation, "B", null, "Normal")).Id;
            await CreateDependencyService(db).AddDependencyAsync(taskA, taskB);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Ready);
            await workItems.TransitionStatusAsync(taskB, WorkItemStatus.Running);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(
            TaskReadinessStatus.WaitingForDependency,
            await CreateReadinessService(verifyDb).GetReadinessAsync(taskA));
    }

    [Fact]
    public async Task GetReadinessAsync_OwnStatusRunning_ReturnsRunning()
    {
        var projectId = await SeedProjectAsync();
        Guid taskA;
        await using (var db = new FactoryDbContext(_options))
        {
            taskA = (await CreateWorkItemService(db).CreateAsync(
                projectId, WorkItemType.Implementation, "A", null, "Normal")).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = CreateWorkItemService(db);
            await workItems.TransitionStatusAsync(taskA, WorkItemStatus.Ready);
            await workItems.TransitionStatusAsync(taskA, WorkItemStatus.Running);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(TaskReadinessStatus.Running, await CreateReadinessService(verifyDb).GetReadinessAsync(taskA));
    }

    [Fact]
    public async Task GetReadinessAsync_UnknownTask_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateReadinessService(db).GetReadinessAsync(Guid.NewGuid()));
    }
}
