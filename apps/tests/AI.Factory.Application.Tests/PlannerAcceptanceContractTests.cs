using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
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

/// <summary>
/// Phase 18.22 §2/§4/§5/§24 — a newly planned task must declare
/// machine-checkable evidence of completion, enforced at both boundaries:
/// structurally in <see cref="PlannerResultMapper"/> when an untrusted
/// candidate plan arrives, and again in <see cref="ProjectPlanService"/>
/// before anything is persisted.
///
/// This exists because Phase 18.21 proved build/test cannot distinguish "the
/// agent did the work" from "the agent did nothing to an already-green repo".
/// A plan that never states observable evidence would silently reopen that gap.
/// </summary>
public sealed class PlannerAcceptanceContractTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public PlannerAcceptanceContractTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------- Planner mapper (§4) ----------------

    private static StructuredAgentResult CandidateWith(string taskJson) =>
        new(
            StructuredAgentResult.CurrentVersion, StructuredResultStatus.Succeeded, "s", "o",
            "{\"plan\":{\"version\":1,\"summary\":\"s\",\"tasks\":[" + taskJson + "]}}",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);

    [Fact]
    public void Map_TaskWithValidAcceptanceCriteria_IsAccepted_AndCarriesTheContract()
    {
        var result = new PlannerResultMapper().Map(Guid.NewGuid(), CandidateWith(
            """
            {"planKey":"a","title":"A","type":"Implementation","dependsOn":[],
             "acceptanceCriteria":{"version":1,"criteria":[
               {"type":"ExpectedTextPresent","path":"src/Greeting.cs","value":"Hello, Factory"}]}}
            """));

        var task = Assert.Single(result.Tasks);
        Assert.NotNull(task.AcceptanceCriteria);
        Assert.Contains("ExpectedTextPresent", task.AcceptanceCriteria);
        Assert.Contains("Hello, Factory", task.AcceptanceCriteria);
    }

    [Fact]
    public void Map_TaskWithoutAcceptanceCriteria_IsRejected()
    {
        var ex = Assert.Throws<PlannerCandidatePlanException>(() =>
            new PlannerResultMapper().Map(Guid.NewGuid(), CandidateWith(
                """{"planKey":"a","title":"A","type":"Implementation","dependsOn":[]}""")));

        Assert.Contains("acceptanceCriteria", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_TaskWithUnsupportedCriterionType_IsRejected()
    {
        // The one thing acceptance criteria must never become: a way to smuggle
        // commands past the Factory.
        var ex = Assert.Throws<PlannerCandidatePlanException>(() =>
            new PlannerResultMapper().Map(Guid.NewGuid(), CandidateWith(
                """
                {"planKey":"a","title":"A","type":"Implementation","dependsOn":[],
                 "acceptanceCriteria":{"version":1,"criteria":[
                   {"type":"RunShellCommand","path":".","value":"rm -rf /"}]}}
                """)));

        Assert.Contains("RunShellCommand", ex.Message);
    }

    [Theory]
    // criteria array present but empty
    [InlineData("""{"version":1,"criteria":[]}""")]
    // unsupported envelope version
    [InlineData("""{"version":9,"criteria":[{"type":"FileExists","path":"a.md"}]}""")]
    // criterion missing its required path
    [InlineData("""{"version":1,"criteria":[{"type":"FileExists"}]}""")]
    // text criterion missing its required value
    [InlineData("""{"version":1,"criteria":[{"type":"ExpectedTextPresent","path":"a.md"}]}""")]
    public void Map_TaskWithMalformedAcceptanceCriteria_IsRejected(string criteria)
    {
        Assert.Throws<PlannerCandidatePlanException>(() =>
            new PlannerResultMapper().Map(Guid.NewGuid(), CandidateWith(
                "{\"planKey\":\"a\",\"title\":\"A\",\"type\":\"Implementation\",\"dependsOn\":[],\"acceptanceCriteria\":" + criteria + "}")));
    }

    [Fact]
    public void Map_AcceptanceCriteriaThatIsNotAnObject_IsRejected()
    {
        Assert.Throws<PlannerCandidatePlanException>(() =>
            new PlannerResultMapper().Map(Guid.NewGuid(), CandidateWith(
                """{"planKey":"a","title":"A","type":"Implementation","dependsOn":[],"acceptanceCriteria":"make it work"}""")));
    }

    // ---------------- Plan persistence (§5) ----------------

    private ProjectPlanService CreateService(FactoryDbContext db) => new(
        new ProjectRepository(db), new WorkItemRepository(db), new TaskDependencyResolver(),
        new AgentDefinitionRepository(db), new NoOpTransactionManager(db),
        NullLogger<ProjectPlanService>.Instance);

    /// <summary>The plan path is exercised directly here; transactional behavior has its own coverage.</summary>
    private sealed class NoOpTransactionManager : IDatabaseTransactionManager
    {
        private readonly FactoryDbContext _db;

        public NoOpTransactionManager(FactoryDbContext db) => _db = db;

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            var result = await operation(cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return result;
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            await operation(cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Guid> SeedPlanningProjectAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync($"Plan {Guid.NewGuid():N}", "d");
        project.Status = ProjectStatus.Planning;
        await repo.SaveChangesAsync();

        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Impl", AgentType = nameof(WorkItemType.Implementation), Enabled = true, SystemPrompt = "x"
        });

        return project.Id;
    }

    private static PlannedWorkItemInput Task_(string key, string? criteria) =>
        new(key, $"Title {key}", "d", WorkItemType.Implementation, "Normal", Array.Empty<string>(), criteria);

    private const string ValidCriteria =
        """{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}""";

    [Fact]
    public async Task CreatePlanAsync_TaskWithoutCriteria_IsRejected_AndPersistsNothing()
    {
        var projectId = await SeedPlanningProjectAsync();

        await using var db = new FactoryDbContext(_options);

        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
                projectId, "s", new[] { Task_("a", ValidCriteria), Task_("b", null) })));

        // §5 — deterministic rejection, no partial plan: not even the valid
        // first task may survive.
        var persisted = await new WorkItemRepository(db).GetByProjectIdAsync(projectId);
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task CreatePlanAsync_TaskWithLegacyFreeTextCriteria_IsRejected()
    {
        // Free text is exactly what this phase replaces: it is not
        // machine-checkable, so it cannot satisfy a NEW plan (§2), even though
        // historical rows carrying it stay valid (§6, covered below).
        var projectId = await SeedPlanningProjectAsync();

        await using var db = new FactoryDbContext(_options);

        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
                projectId, "s", new[] { Task_("a", "The greeting should look friendly.") })));
    }

    [Fact]
    public async Task CreatePlanAsync_EveryTaskHasCriteria_PersistsThemOntoTheWorkItems()
    {
        var projectId = await SeedPlanningProjectAsync();

        await using var db = new FactoryDbContext(_options);
        var result = await CreateService(db).CreatePlanAsync(new ProjectPlanRequest(
            projectId, "s", new[] { Task_("a", ValidCriteria), Task_("b", ValidCriteria) }));

        Assert.Equal(2, result.Tasks.Count);

        var persisted = await new WorkItemRepository(db).GetByProjectIdAsync(projectId);
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, w => Assert.False(string.IsNullOrWhiteSpace(w.AcceptanceCriteria)));

        // The contract that reaches storage is the one the verifier can read.
        Assert.All(persisted, w => Assert.Equal(
            AcceptanceCriteriaParseStatus.Valid,
            AcceptanceCriteriaContract.Parse(w.AcceptanceCriteria).Status));
    }

    [Fact]
    public async Task HistoricalWorkItemWithoutCriteria_RemainsValid()
    {
        // §6 — the new rule governs plan creation, not rows that predate it.
        // A WorkItem written directly (as every pre-18.22 row was) still loads
        // and is still evaluable: the verifier reports NotSpecified, which
        // Phase 18.21 maps to the historical build/test-only semantics.
        var projectId = await SeedPlanningProjectAsync();

        await using var db = new FactoryDbContext(_options);
        var repo = new WorkItemRepository(db);

        var legacy = new WorkItem
        {
            ProjectId = projectId,
            Title = "Legacy",
            Description = "predates acceptance contracts",
            Type = WorkItemType.Implementation,
            Status = WorkItemStatus.Ready.ToString(),
            Priority = "Normal",
            AcceptanceCriteria = null
        };

        await repo.AddAsync(legacy);
        await db.SaveChangesAsync();

        var reloaded = await repo.GetByIdAsync(legacy.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.AcceptanceCriteria);
        Assert.Equal(
            AcceptanceCriteriaParseStatus.NotSpecified,
            AcceptanceCriteriaContract.Parse(reloaded.AcceptanceCriteria).Status);
    }
}
