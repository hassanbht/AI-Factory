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
        new AgentRunRepository(db),
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

    // ---------------- Phase 18.13 — WorkItem-based cancellation semantics ----------------

    [Theory]
    [InlineData(nameof(WorkItemStatus.Ready))]
    [InlineData(nameof(WorkItemStatus.Running))]
    [InlineData(nameof(WorkItemStatus.Repairing))]
    public async Task RequestCancellationAsync_TransitionsWorkItem_ToCancelled_FromAnyNonTerminalStatus(string status)
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var workItem = await db.WorkItems.SingleAsync(x => x.Id == _workItemId);
            workItem.Status = status;
            await db.SaveChangesAsync();
        }

        await using var serviceDb = new FactoryDbContext(_options);
        var service = MakeService(serviceDb);
        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));

        await service.RequestCancellationAsync(result.RunId);

        await using var verifyDb = new FactoryDbContext(_options);
        var cancelled = await verifyDb.WorkItems.SingleAsync(x => x.Id == _workItemId);
        Assert.Equal(WorkItemStatus.Cancelled.ToString(), cancelled.Status);
    }

    [Fact]
    public async Task RequestCancellationAsync_IsIdempotent_WhenCalledTwice()
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var workItem = await db.WorkItems.SingleAsync(x => x.Id == _workItemId);
            workItem.Status = WorkItemStatus.Ready.ToString();
            await db.SaveChangesAsync();
        }

        await using var serviceDb = new FactoryDbContext(_options);
        var service = MakeService(serviceDb);
        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));

        await service.RequestCancellationAsync(result.RunId);
        // Second call must not throw and must not resurrect/alter the already-cancelled WorkItem.
        await service.RequestCancellationAsync(result.RunId);

        await using var verifyDb = new FactoryDbContext(_options);
        var workItemAfter = await verifyDb.WorkItems.SingleAsync(x => x.Id == _workItemId);
        Assert.Equal(WorkItemStatus.Cancelled.ToString(), workItemAfter.Status);
    }

    private async Task<Guid> SeedAgentDefinitionAsync(FactoryDbContext db)
    {
        var definition = new AgentDefinition { Name = "Impl", AgentType = "Implementation", SystemPrompt = "x" };
        db.AgentDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition.Id;
    }

    [Fact]
    public async Task RequestCancellationAsync_Throws_AndDoesNotResurrectWorkItem_WhenAgentRunAlreadyCompletedIt()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);
        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));
        var agentDefinitionId = await SeedAgentDefinitionAsync(db);

        // Simulate the race: the AgentRun pipeline finished and completed the
        // WorkItem before the cancel request is processed.
        var agentRun = new AgentRun
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = agentDefinitionId,
            WorkItemId = _workItemId,
            ProjectId = _projectId,
            Status = AgentRunStatus.Succeeded
        };
        db.AgentRuns.Add(agentRun);

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        run.AgentRunId = agentRun.Id;

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == _workItemId);
        workItem.Status = WorkItemStatus.Completed.ToString();

        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => service.RequestCancellationAsync(result.RunId));

        await using var verifyDb = new FactoryDbContext(_options);
        var untouched = await verifyDb.WorkItems.SingleAsync(x => x.Id == _workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), untouched.Status);
    }

    [Fact]
    public async Task GetAsync_ProjectsCompletedWorkItemStatus_OntoDispatchedRun()
    {
        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);
        var result = await service.StartAsync(new StartOrchestrationCommand(_projectId, _workItemId));
        var agentDefinitionId = await SeedAgentDefinitionAsync(db);

        var agentRun = new AgentRun
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = agentDefinitionId,
            WorkItemId = _workItemId,
            ProjectId = _projectId,
            Status = AgentRunStatus.Succeeded
        };
        db.AgentRuns.Add(agentRun);

        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        run.AgentRunId = agentRun.Id;
        run.Status = OrchestrationRunStatus.Executing;

        var workItem = await db.WorkItems.SingleAsync(x => x.Id == _workItemId);
        workItem.Status = WorkItemStatus.Completed.ToString();

        await db.SaveChangesAsync();

        await using var readDb = new FactoryDbContext(_options);
        var readService = MakeService(readDb);
        var projected = await readService.GetAsync(result.RunId);

        Assert.Equal(OrchestrationRunStatus.Completed, projected!.Status);

        // Read-time projection must never persist — the stored row stays Executing.
        await using var verifyDb = new FactoryDbContext(_options);
        var stored = await verifyDb.OrchestrationRuns.SingleAsync(x => x.Id == result.RunId);
        Assert.Equal(OrchestrationRunStatus.Executing, stored.Status);
    }
}
