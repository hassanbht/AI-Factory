using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.1.6 — IAgentRunService (the use-case behind the AgentRun API) coverage.</summary>
public sealed class AgentRunServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly OperationalMetrics _metrics = new();

    public AgentRunServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AgentDefinition> SeedDefinitionAsync(bool enabled = true)
    {
        await using var db = new FactoryDbContext(_options);
        var definition = new AgentDefinition
        {
            Name = "Coder",
            AgentType = "Coding",
            Enabled = enabled
        };
        return await new AgentDefinitionRepository(db).CreateAsync(definition);
    }

    private AgentRunService CreateService(FactoryDbContext db) => new(
        new AgentRunRepository(db),
        new AgentDefinitionRepository(db),
        _metrics,
        NullLogger<AgentRunService>.Instance);

    [Fact]
    public async Task CreateAsync_ValidDefinition_CreatesRunInQueuedState_AndIncrementsMetric()
    {
        var definition = await SeedDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        var run = await service.CreateAsync(new CreateAgentRunCommand(definition.Id, ProjectId: null, Input: "do it"));

        Assert.Equal(AgentRunStatus.Queued, run.Status);
        Assert.Equal(1, _metrics.GetSnapshot().AgentRunsCreated);
    }

    [Fact]
    public async Task CreateAsync_UnknownDefinition_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.CreateAsync(new CreateAgentRunCommand(Guid.NewGuid(), null, "x")));
    }

    [Fact]
    public async Task CreateAsync_EmptyDefinitionId_ThrowsValidation()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(new CreateAgentRunCommand(Guid.Empty, null, "x")));
    }

    [Fact]
    public async Task CreateAsync_DisabledDefinition_ThrowsValidation()
    {
        var definition = await SeedDefinitionAsync(enabled: false);

        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateAsync(new CreateAgentRunCommand(definition.Id, null, "x")));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForUnknownId()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        Assert.Null(await service.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_FiltersByAgentDefinitionId()
    {
        var definitionA = await SeedDefinitionAsync();
        var definitionB = await SeedDefinitionAsync();

        await using (var db = new FactoryDbContext(_options))
        {
            var service = CreateService(db);
            await service.CreateAsync(new CreateAgentRunCommand(definitionA.Id, null, "a"));
            await service.CreateAsync(new CreateAgentRunCommand(definitionB.Id, null, "b"));
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var verifyService = CreateService(verifyDb);
        var page = await verifyService.ListAsync(new AgentRunQuery(AgentDefinitionId: definitionA.Id));

        Assert.Single(page.Items);
        Assert.Equal(definitionA.Id, page.Items[0].AgentDefinitionId);
    }

    [Fact]
    public async Task CancelAsync_QueuedRun_TransitionsToCancelled_AndIncrementsMetric()
    {
        var definition = await SeedDefinitionAsync();
        AgentRun run;

        await using (var db = new FactoryDbContext(_options))
        {
            run = await CreateService(db).CreateAsync(new CreateAgentRunCommand(definition.Id, null, "x"));
        }

        await using var db2 = new FactoryDbContext(_options);
        await CreateService(db2).CancelAsync(run.Id);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);
        Assert.Equal(AgentRunStatus.Cancelled, reloaded!.Status);
        Assert.Equal(1, _metrics.GetSnapshot().AgentRunsCancelled);
    }

    [Fact]
    public async Task CancelAsync_UnknownRun_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);

        await Assert.ThrowsAsync<NotFoundException>(() => service.CancelAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CancelAsync_AlreadyTerminalRun_ThrowsConflict()
    {
        var definition = await SeedDefinitionAsync();
        AgentRun run;

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            run = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
            run.TransitionTo(AgentRunStatus.Queued);
            run.TransitionTo(AgentRunStatus.Running);
            run.TransitionTo(AgentRunStatus.Succeeded);
            await repo.UpdateAsync(run);
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db2).CancelAsync(run.Id));
    }

    [Fact]
    public async Task CancelAsync_RunningRun_ThrowsConflict()
    {
        var definition = await SeedDefinitionAsync();
        AgentRun run;

        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new AgentRunRepository(db);
            run = await repo.CreateAsync(new AgentRun { AgentDefinitionId = definition.Id });
            run.TransitionTo(AgentRunStatus.Queued);
            run.TransitionTo(AgentRunStatus.Running);
            await repo.UpdateAsync(run);
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db2).CancelAsync(run.Id));
    }
}
