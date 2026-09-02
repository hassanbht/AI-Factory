using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OrchestrationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private Guid _workItemId;

    public OrchestrationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Orchestration Service Test",
            Slug = "orchestration-service-test",
            RootPath = string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        _workItemId = Guid.NewGuid();
        db.WorkItems.Add(new WorkItem
        {
            Id = _workItemId,
            ProjectId = _projectId,
            Title = "Do the thing",
            Status = WorkItemStatus.Draft.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private OrchestrationService MakeService(FactoryDbContext db) => new(
        new ProjectRepository(db),
        new WorkItemRepository(db),
        new OrchestrationRunRepository(db),
        new OrchestrationOptions { WorkspaceRoot = Path.GetTempPath(), MaxAttempts = 3 });

    [Fact]
    public async Task StartAsync_CreatesPendingRun_AndReturnsImmediately()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));

        Assert.Equal("Pending", result.Status);
        Assert.Equal($"/api/orchestration-runs/{result.RunId}", result.StatusUrl);

        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        Assert.Equal(OrchestrationRunStatus.Pending, persisted.Status);
        Assert.Equal(_workItemId, persisted.WorkItemId);
    }

    [Fact]
    public async Task StartAsync_Throws_WhenProjectDoesNotExist()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.StartAsync(new StartOrchestrationCommand(Guid.NewGuid(), _workItemId)));
    }

    [Fact]
    public async Task StartAsync_Throws_WhenTaskDoesNotExist()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.StartAsync(new StartOrchestrationCommand(_projectId, Guid.NewGuid())));
    }

    [Fact]
    public async Task RequestCancellationAsync_SetsCancellationRequestedAtUtc()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));
        await service.RequestCancellationAsync(result.RunId);

        var persisted = await db.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        Assert.NotNull(persisted.CancellationRequestedAtUtc);
    }

    [Fact]
    public async Task RequestCancellationAsync_Throws_ForAlreadyTerminalRun()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        run.Status = OrchestrationRunStatus.Completed;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => service.RequestCancellationAsync(result.RunId));
    }
}
