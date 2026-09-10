using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.2 §13/§15 — AgentRun.ProjectId/WorkItemId (pre-existing, nullable
/// scalar fields) remain backward compatible: an AgentRun with neither set
/// still persists, one linked to a Project persists, one linked to a Task
/// persists, and an existing structured-result payload (Phase 18.1.8) is
/// unaffected by either link.
/// </summary>
public sealed class AgentRunProjectTaskLinkageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public AgentRunProjectTaskLinkageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedAgentDefinitionAsync()
    {
        var definition = new AgentDefinition
        {
            Name = "Coder",
            Description = "Implements WorkItems",
            AgentType = "Coding",
            SystemPrompt = "You are a coding agent."
        };

        await using var db = new FactoryDbContext(_options);
        var created = await new AgentDefinitionRepository(db).CreateAsync(definition);
        return created.Id;
    }

    [Fact]
    public async Task AgentRun_WithoutProjectOrTask_PersistsAsBefore()
    {
        var agentDefinitionId = await SeedAgentDefinitionAsync();
        var run = new AgentRun { AgentDefinitionId = agentDefinitionId };

        await using (var db = new FactoryDbContext(_options))
        {
            await new AgentRunRepository(db).CreateAsync(run);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.ProjectId);
        Assert.Null(reloaded.WorkItemId);
    }

    [Fact]
    public async Task AgentRun_LinkedToProject_Persists()
    {
        var agentDefinitionId = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Linked Project", "d");
        var run = new AgentRun { AgentDefinitionId = agentDefinitionId, ProjectId = project.Id };
        await new AgentRunRepository(db).CreateAsync(run);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(project.Id, reloaded!.ProjectId);
    }

    [Fact]
    public async Task AgentRun_LinkedToTask_Persists()
    {
        var agentDefinitionId = await SeedAgentDefinitionAsync();

        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Task-Linked Project", "d");
        var task = await new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db))
            .CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");
        var run = new AgentRun { AgentDefinitionId = agentDefinitionId, ProjectId = project.Id, WorkItemId = task.Id };
        await new AgentRunRepository(db).CreateAsync(run);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(task.Id, reloaded!.WorkItemId);
    }

    [Fact]
    public async Task AgentRun_LinkedToProject_StructuredResult_RemainsIntact()
    {
        var agentDefinitionId = await SeedAgentDefinitionAsync();
        const string structuredResultJson = "{\"summary\":\"done\",\"filesChanged\":[]}";

        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Structured Project", "d");
        var run = new AgentRun
        {
            AgentDefinitionId = agentDefinitionId,
            ProjectId = project.Id,
            StructuredResult = structuredResultJson,
            StructuredResultVersion = 1
        };
        await new AgentRunRepository(db).CreateAsync(run);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new AgentRunRepository(verifyDb).GetByIdAsync(run.Id);

        Assert.Equal(structuredResultJson, reloaded!.StructuredResult);
        Assert.Equal(1, reloaded.StructuredResultVersion);
        Assert.Equal(project.Id, reloaded.ProjectId);
    }
}
