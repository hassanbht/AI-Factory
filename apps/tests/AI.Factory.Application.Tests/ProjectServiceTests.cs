using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §15 — Project creation/retrieval/update/lifecycle.</summary>
public sealed class ProjectServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private ProjectService CreateService(FactoryDbContext db) => new(new ProjectRepository(db));

    [Fact]
    public async Task CreateAsync_ValidName_PersistsAsCreated()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync("My Project", "desc");

        Assert.Equal(ProjectStatus.Created, project.Status);
        Assert.Equal("my-project", project.Slug);
    }

    [Fact]
    public async Task GetAsync_ExistingProject_ReturnsIt()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Findable", "desc")).Id;
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var found = await CreateService(verifyDb).GetAsync(id);
        Assert.NotNull(found);
        Assert.Equal("Findable", found!.Name);
    }

    [Fact]
    public async Task GetAsync_UnknownProject_ReturnsNull()
    {
        await using var db = new FactoryDbContext(_options);
        Assert.Null(await CreateService(db).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedProjects()
    {
        await using (var db = new FactoryDbContext(_options))
        {
            var service = CreateService(db);
            await service.CreateAsync("P1", "d");
            await service.CreateAsync("P2", "d");
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var all = await CreateService(verifyDb).ListAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task TransitionStatusAsync_ValidTransition_Persists()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Transitionable", "d")).Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).TransitionStatusAsync(id, ProjectStatus.Analyzing);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await CreateService(verifyDb).GetAsync(id);
        Assert.Equal(ProjectStatus.Analyzing, reloaded!.Status);
    }

    [Fact]
    public async Task TransitionStatusAsync_InvalidTransition_Throws()
    {
        Guid id;
        await using (var db = new FactoryDbContext(_options))
        {
            id = (await CreateService(db).CreateAsync("Blocked", "d")).Id;
        }

        await using var db2 = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<InvalidProjectTransitionException>(
            () => CreateService(db2).TransitionStatusAsync(id, ProjectStatus.Testing));
    }

    [Fact]
    public async Task TransitionStatusAsync_UnknownProject_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService(db).TransitionStatusAsync(Guid.NewGuid(), ProjectStatus.Analyzing));
    }

    /// <summary>Phase 18.11 §2/§5 — the generic status endpoint can never produce Completed, even from the one status (HumanReview) where it was previously a structurally valid domain transition.</summary>
    [Fact]
    public async Task TransitionStatusAsync_ToCompleted_IsAlwaysRejected_EvenFromHumanReview()
    {
        var id = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview, "P-BlockedCompletion");

        await using var db = new FactoryDbContext(_options);
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService(db).TransitionStatusAsync(id, ProjectStatus.Completed));

        Assert.Contains("approval", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- ApproveAsync ----------------

    private async Task<Guid> SeedProjectAtStatusAsync(ProjectStatus status, string name)
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync(name, "d");
        project.Status = status;
        await repo.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task ApproveAsync_FromHumanReview_TransitionsToCompleted()
    {
        var id = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview, "P-Approve");

        await using var db = new FactoryDbContext(_options);
        var approved = await CreateService(db).ApproveAsync(id, actor: "reviewer@example.com");

        Assert.Equal(ProjectStatus.Completed, approved.Status);

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new ProjectRepository(verifyDb).GetByIdAsync(id);
        Assert.Equal(ProjectStatus.Completed, reloaded!.Status);
    }

    [Theory]
    [InlineData(ProjectStatus.Created)]
    [InlineData(ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Specification)]
    [InlineData(ProjectStatus.Architecture)]
    [InlineData(ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Implementation)]
    [InlineData(ProjectStatus.Testing)]
    [InlineData(ProjectStatus.Reviewing)]
    [InlineData(ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Cancelled)]
    public async Task ApproveAsync_FromAnyOtherStatus_IsRejected(ProjectStatus status)
    {
        var id = await SeedProjectAtStatusAsync(status, $"P-Approve-{status}");

        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService(db).ApproveAsync(id));

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new ProjectRepository(verifyDb).GetByIdAsync(id);
        Assert.Equal(status, reloaded!.Status);
    }

    [Fact]
    public async Task ApproveAsync_Repeated_IsIdempotent()
    {
        var id = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview, "P-RepeatApprove");

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).ApproveAsync(id);
        }

        // A second, independent approval request against the now-Completed
        // Project must be a safe no-op — never throw, never re-transition.
        await using var db2 = new FactoryDbContext(_options);
        var second = await CreateService(db2).ApproveAsync(id);
        Assert.Equal(ProjectStatus.Completed, second.Status);
    }

    [Fact]
    public async Task ApproveAsync_ConcurrentRequests_ExactlyOneSucceeds_ResultIsCompleted()
    {
        var id = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview, "P-ConcurrentApprove");

        await using var dbA = new FactoryDbContext(_options);
        await using var dbB = new FactoryDbContext(_options);

        var results = await Task.WhenAll(
            CreateService(dbA).ApproveAsync(id),
            CreateService(dbB).ApproveAsync(id));

        Assert.All(results, x => Assert.Equal(ProjectStatus.Completed, x.Status));

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new ProjectRepository(verifyDb).GetByIdAsync(id);
        Assert.Equal(ProjectStatus.Completed, reloaded!.Status);
    }

    [Fact]
    public async Task ApproveAsync_UnknownProject_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);
        await Assert.ThrowsAsync<NotFoundException>(() => CreateService(db).ApproveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ApproveAsync_DoesNotMutateWorkItemsOrCreateAgentRuns()
    {
        var id = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview, "P-NoSideEffects");

        Guid workItemId;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new AI.Factory.Application.Tasks.WorkItemService(
                new AI.Factory.Infrastructure.Persistence.WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(id, WorkItemType.Implementation, "A", null, "Normal");
            workItemId = item.Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await CreateService(db).ApproveAsync(id);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var workItem = await new AI.Factory.Infrastructure.Persistence.WorkItemRepository(verifyDb).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Draft.ToString(), workItem!.Status);
        Assert.Equal(0, await verifyDb.AgentRuns.CountAsync(x => x.ProjectId == id));
    }
}
