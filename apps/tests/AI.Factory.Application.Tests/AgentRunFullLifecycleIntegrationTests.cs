using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.1.6 §22 — the second (conditional) integration flow: Queued ->
/// Worker -> FakeAgentRuntime -> Succeeded -> GET shows Succeeded. Api.Tests'
/// ApiTestFactory only hosts the Api process, which never registers
/// AgentRunWorker (only Worker/Program.cs does) — there is no existing
/// in-process host that runs both the HTTP API and the background Worker
/// together. This test proves the same path one layer down instead: the
/// exact sequence AgentRunWorker.PollOnceAsync performs (claim -> orchestrator
/// .ExecuteAsync) is invoked directly against a real AgentRunService +
/// AgentOrchestrator + FakeAgentRuntime, all sharing one in-memory SQLite
/// database — deterministic, no real Hermes, no BackgroundService needed.
/// </summary>
public sealed class AgentRunFullLifecycleIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunFullLifecycleIntegrationTests()
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

    [Fact]
    public async Task CreateRun_ThenClaimAndOrchestrate_ReachesSucceeded()
    {
        Guid definitionId;
        await using (var db = new FactoryDbContext(_options))
        {
            var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
            {
                Name = "Coder",
                AgentType = "Coding"
            });
            definitionId = definition.Id;
        }

        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            var runService = new AgentRunService(
                new AgentRunRepository(db),
                new AgentDefinitionRepository(db),
                new OperationalMetrics(),
                NullLogger<AgentRunService>.Instance);

            var created = await runService.CreateAsync(new CreateAgentRunCommand(definitionId, null, "do the thing"));
            Assert.Equal(AgentRunStatus.Queued, created.Status);
            runId = created.Id;
        }

        // Simulate exactly what AgentRunWorker.PollOnceAsync does: claim, then orchestrate.
        AgentRun? claimed;
        await using (var db = new FactoryDbContext(_options))
        {
            claimed = await new AgentRunRepository(db).TryClaimNextPendingAsync();
        }
        Assert.NotNull(claimed);
        Assert.Equal(runId, claimed!.Id);
        Assert.Equal(AgentRunStatus.Running, claimed.Status);

        await using (var db = new FactoryDbContext(_options))
        {
            var orchestrator = new AgentOrchestrator(
                new AgentRunRepository(db),
                new ProjectRepository(db),
                new FakeAgentToolRegistry("ReadArtifact"),
                new FakeAgentMemory(),
                new FakeAgentRuntime(FakeAgentRuntime.Mode.Success),
                new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
                new AgentInstructionBuilder(),
                new StructuredAgentResultParser(),
                NullLogger<AgentOrchestrator>.Instance);

            var result = await orchestrator.ExecuteAsync(runId);
            Assert.True(result.Success);
            Assert.Equal(AgentRunStatus.Succeeded, result.FinalStatus);
        }

        await using (var db = new FactoryDbContext(_options))
        {
            var runService = new AgentRunService(
                new AgentRunRepository(db),
                new AgentDefinitionRepository(db),
                new OperationalMetrics(),
                NullLogger<AgentRunService>.Instance);

            var final = await runService.GetAsync(runId);
            Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        }
    }
}
