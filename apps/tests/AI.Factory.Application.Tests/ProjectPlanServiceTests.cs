using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.5 §19 — plan generation, validation, normalization, persistence, idempotency, and lifecycle coverage.</summary>
public sealed class ProjectPlanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectPlanServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static ProjectPlanService CreateService(FactoryDbContext db) => new(
        new ProjectRepository(db),
        new WorkItemRepository(db),
        new TaskDependencyResolver(),
        new AgentDefinitionRepository(db),
        new DatabaseTransactionManager(db, new OperationalMetrics(), NullLogger<DatabaseTransactionManager>.Instance),
        NullLogger<ProjectPlanService>.Instance);

    private async Task<Guid> SeedPlanningProjectAsync(string name = "Planner Project")
    {
        await using var db = new FactoryDbContext(_options);
        var service = new ProjectService(new ProjectRepository(db));
        var project = await service.CreateAsync(name, "d");
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Analyzing);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Specification);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Architecture);
        await service.TransitionStatusAsync(project.Id, ProjectStatus.Planning);
        return project.Id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = $"Agent-{agentType}",
            AgentType = agentType,
            SystemPrompt = "x"
        });
    }

    private const string ValidCriteria =
        """{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}""";

    /// <summary>
    /// Phase 18.22 §2 — every task in a new plan must carry a machine-checkable
    /// acceptance contract, so this shared helper supplies one by default. Pass
    /// <paramref name="acceptanceCriteria"/> explicitly to exercise the missing
    /// or malformed cases.
    /// </summary>
    private static PlannedWorkItemInput Task_(
        string key,
        IReadOnlyList<string>? dependsOn = null,
        WorkItemType type = WorkItemType.Implementation,
        string priority = "Normal",
        string? acceptanceCriteria = ValidCriteria) =>
        new(key, $"Title {key}", $"Description {key}", type, priority, dependsOn ?? Array.Empty<string>(), acceptanceCriteria);

    // ---------------- Generation ----------------

    [Fact]
    public async Task CreatePlanAsync_ValidInput_PersistsExpectedTaskSet()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        var result = await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
            projectId, "Summary", new[] { Task_("a"), Task_("b", new[] { "a" }) }));

        Assert.Equal(2, result.Tasks.Count);
        Assert.Contains(result.Tasks, t => t.PlanKey == "a" && t.Type == WorkItemType.Implementation && t.Priority == "Normal");
        Assert.Contains(result.Tasks, t => t.PlanKey == "b" && t.DependsOn.SequenceEqual(new[] { "a" }));
    }

    [Fact]
    public async Task CreatePlanAsync_SameCandidatePlan_DifferentProjects_ProducesSameHash()
    {
        var projectA = await SeedPlanningProjectAsync("Project A");
        var projectB = await SeedPlanningProjectAsync("Project B");
        await SeedAgentDefinitionAsync("Implementation");

        var tasks = new[] { Task_("a"), Task_("b", new[] { "a" }) };

        await using var db = new FactoryDbContext(_options);
        var resultA = await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(projectA, null, tasks));
        var resultB = await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(projectB, null, tasks));

        Assert.Equal(resultA.PlanHash, resultB.PlanHash);
    }

    // ---------------- Validation ----------------

    [Fact]
    public async Task CreatePlanAsync_MissingPlanKey_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("") })));
    }

    [Fact]
    public async Task CreatePlanAsync_DuplicatePlanKey_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("a"), Task_("a") })));
    }

    [Fact]
    public async Task CreatePlanAsync_MissingDependencyTarget_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("a", new[] { "ghost" }) })));
    }

    [Fact]
    public async Task CreatePlanAsync_SelfDependency_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("a", new[] { "a" }) })));
    }

    [Fact]
    public async Task CreatePlanAsync_CircularDependency_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null,
                new[] { Task_("a", new[] { "b" }), Task_("b", new[] { "c" }), Task_("c", new[] { "a" }) })));
    }

    [Fact]
    public async Task CreatePlanAsync_NoAgentDefinitionForType_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        // No AgentDefinition seeded for Implementation.

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("a") })));
    }

    // ---------------- Normalization ----------------

    [Fact]
    public async Task CreatePlanAsync_TaskAndDependencyOrdering_IsDeterministic()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using var db = new FactoryDbContext(_options);
        var result = await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
            projectId, null,
            new[] { Task_("z"), Task_("a"), Task_("m", new[] { "z", "a", "a" }) }));

        Assert.Equal(new[] { "a", "m", "z" }, result.Tasks.Select(t => t.PlanKey));
        Assert.Equal(new[] { "a", "z" }, result.Tasks.Single(t => t.PlanKey == "m").DependsOn);
    }

    // ---------------- Persistence ----------------

    [Fact]
    public async Task CreatePlanAsync_ValidPlan_PersistsAllWorkItemsAndDependencies()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
                projectId, null, new[] { Task_("a"), Task_("b", new[] { "a" }) }));
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var items = await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId);

        Assert.Equal(2, items.Count);
        var b = items.Single(x => x.PlanKey == "b");
        Assert.Single(b.Dependencies);
        Assert.Equal(items.Single(x => x.PlanKey == "a").Id, b.Dependencies.Single().DependsOnWorkItemId);
        Assert.All(items, x => Assert.Equal(WorkItemStatus.Ready.ToString(), x.Status));
    }

    [Fact]
    public async Task CreatePlanAsync_ValidationFailure_LeavesNoPartialTasks()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using (var db = new FactoryDbContext(_options))
        {
            await Assert.ThrowsAsync<ValidationException>(() => CreateService(db).CreatePlanAsync(
                new ProjectPlanRequest(projectId, null, new[] { Task_("a"), Task_("b", new[] { "ghost" }) })));
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var items = await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId);
        Assert.Empty(items);
    }

    // ---------------- Idempotency ----------------

    [Fact]
    public async Task CreatePlanAsync_RepeatedIdenticalPlan_ReturnsExistingWithoutDuplicating()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");
        var tasks = new[] { Task_("a"), Task_("b", new[] { "a" }) };

        await using var db1 = new FactoryDbContext(_options);
        var first = await CreateService(db1).CreatePlanAsync(new ProjectPlanRequest(projectId, null, tasks));

        await using var db2 = new FactoryDbContext(_options);
        var second = await CreateService(db2).CreatePlanAsync(new ProjectPlanRequest(projectId, null, tasks));

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.Tasks.Select(t => t.WorkItemId).OrderBy(x => x), second.Tasks.Select(t => t.WorkItemId).OrderBy(x => x));

        await using var verifyDb = new FactoryDbContext(_options);
        Assert.Equal(2, (await new WorkItemRepository(verifyDb).GetByProjectIdAsync(projectId)).Count);
    }

    [Fact]
    public async Task CreatePlanAsync_DifferentPlanForAlreadyPlannedProject_Rejected()
    {
        var projectId = await SeedPlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        await using (var db1 = new FactoryDbContext(_options))
        {
            await CreateService(db1).CreatePlanAsync(new ProjectPlanRequest(projectId, null, new[] { Task_("a") }));
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db2).CreatePlanAsync(
            new ProjectPlanRequest(projectId, null, new[] { Task_("a"), Task_("b") })));
    }

    // ---------------- Lifecycle ----------------

    [Fact]
    public async Task CreatePlanAsync_ProjectNotInPlanningStatus_Rejected()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await new ProjectService(new ProjectRepository(db)).CreateAsync("Not Planning", "d");
        await SeedAgentDefinitionAsync("Implementation");

        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db).CreatePlanAsync(
            new ProjectPlanRequest(project.Id, null, new[] { Task_("a") })));
    }

    [Fact]
    public async Task GetPlanAsync_NoPlanYet_ReturnsNull()
    {
        var projectId = await SeedPlanningProjectAsync();

        await using var db = new FactoryDbContext(_options);
        Assert.Null(await CreateService(db).GetPlanAsync(projectId));
    }
}
