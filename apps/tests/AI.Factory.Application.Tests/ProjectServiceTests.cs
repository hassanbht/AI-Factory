using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>Always-failing stand-in — Phase 18.15 provisioning-failure tests never need a real filesystem.</summary>
    private sealed class FailingWorkspaceProvisioner : IProjectWorkspaceProvisioner
    {
        public ProvisioningResult Provision(Project project) => ProvisioningResult.Failed("simulated provisioning failure");
    }

    [Fact]
    public async Task CreateAsync_ValidName_PersistsAsCreated()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync("My Project", "desc");

        Assert.Equal(ProjectStatus.Created, project.Status);
        Assert.Equal("my-project", project.Slug);
    }

    // ---------------- Phase 18.16 §8/B1 — GenerateSlug hardening ----------------

    [Theory]
    [InlineData("My Project", "my-project")]
    [InlineData("  Spaced   Out  ", "spaced-out")]
    [InlineData("Hello, World!!!", "hello-world")]
    [InlineData("../../secret", "secret")]
    [InlineData("/absolute/path", "absolute-path")]
    public async Task CreateAsync_GenerateSlug_NormalAndPunctuatedNames_ProducesExpectedSlug(string name, string expectedSlug)
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync(name, "d");
        Assert.Equal(expectedSlug, project.Slug);
    }

    [Fact]
    public async Task CreateAsync_GenerateSlug_WindowsTraversal_IsNeutralized()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync(@"..\..\secret", "d");
        Assert.Equal("secret", project.Slug);
    }

    [Fact]
    public async Task CreateAsync_GenerateSlug_DriveLetterLikeInput_NeverProducesADriveLetterSegment()
    {
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync(@"C:\outside", "d");

        Assert.DoesNotContain(":", project.Slug);
        Assert.DoesNotContain("\\", project.Slug);
        Assert.Equal("c-outside", project.Slug);
    }

    [Theory]
    [InlineData(@"../foo")]
    [InlineData(@"..\foo")]
    [InlineData(@"../../secret")]
    [InlineData(@"C:\outside")]
    [InlineData(@"/absolute/path")]
    [InlineData("a//b\\\\c")]
    [InlineData("café münchen")]
    [InlineData("名前 プロジェクト")]
    public async Task CreateAsync_GenerateSlug_NeverEscapesTheIntendedWorkspaceSegment(string name)
    {
        // Phase 18.16 §8 NO-GO condition — "Project name can still generate
        // an escaping filesystem path": prove the resulting slug (and
        // therefore RootPath = "projects/{slug}") can never contain a
        // separator, "..", or a drive-letter colon, for a representative set
        // of traversal/separator/Unicode inputs including the exact examples
        // named in the phase spec.
        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync(name, "d");

        Assert.DoesNotContain("..", project.Slug);
        Assert.DoesNotContain("/", project.Slug);
        Assert.DoesNotContain("\\", project.Slug);
        Assert.DoesNotContain(":", project.Slug);
        Assert.False(Path.IsPathRooted(project.Slug), $"Slug '{project.Slug}' must not itself be rooted.");
        Assert.False(string.IsNullOrWhiteSpace(project.Slug));

        var rootPath = $"projects/{project.Slug}";
        var normalized = Path.GetFullPath(rootPath);
        Assert.EndsWith(Path.Combine("projects", project.Slug), normalized);
    }

    [Fact]
    public async Task CreateAsync_GenerateSlug_NameNormalizesToEmpty_FallsBackToDeterministicNonEmptySlug()
    {
        const string name = "!!! ... ???";

        await using var db = new FactoryDbContext(_options);
        var project = await CreateService(db).CreateAsync(name, "d");

        Assert.False(string.IsNullOrWhiteSpace(project.Slug));
        Assert.Matches("^project-[0-9a-f]{12}$", project.Slug);
        Assert.Equal(ComputeExpectedFallbackSlug(name), project.Slug);
    }

    [Fact]
    public async Task CreateAsync_GenerateSlug_EmptyFallback_IsDeterministic_SameInputAlwaysMatches()
    {
        const string name = "???";

        await using var db1 = new FactoryDbContext(_options);
        var project1 = await CreateService(db1).CreateAsync(name, "d");

        Assert.Equal(ComputeExpectedFallbackSlug(name), project1.Slug);
    }

    private static string ComputeExpectedFallbackSlug(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return "project-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Phase 18.16 §8/B2 — a stricter slug means two previously-distinct
    /// inputs can now collide (e.g. "My Project!" and "My Project?" both
    /// normalize to "my-project"); the EXISTING slug-uniqueness check already
    /// handles that correctly by throwing ConflictException, exactly as it
    /// would for two Projects literally named the same thing.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NamesCollideAfterSlugNormalization_SecondThrowsConflict()
    {
        await using var db = new FactoryDbContext(_options);
        var service = CreateService(db);
        await service.CreateAsync("My Project!", "d");

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync("My Project?", "d"));
    }

    /// <summary>
    /// Phase 18.15 §3/§10 — a successfully created Project's RootPath must
    /// exist on disk afterward. No AllowedRoot here deliberately: Project's
    /// default RootPath ("projects/{slug}") is relative, and the (unchanged,
    /// Phase 18.14) resolver treats a relative RootPath as CWD-relative
    /// before applying any AllowedRoot boundary — so combining a configured
    /// AllowedRoot with this default relative RootPath in a test would
    /// exercise an unrelated pre-existing limitation rather than this
    /// phase's actual provisioning behavior (see final report).
    /// </summary>
    [Fact]
    public async Task CreateAsync_ProvisionsWorkspaceDirectory_OnDisk()
    {
        var options = new AgentWorkspaceOptions();
        var provisioner = new ProjectWorkspaceProvisioner(
            new ProjectWorkspaceResolver(options), options, NullLogger<ProjectWorkspaceProvisioner>.Instance);

        await using var db = new FactoryDbContext(_options);
        var service = new ProjectService(new ProjectRepository(db), provisioner);

        var project = await service.CreateAsync("Provisioned Project", "desc");
        var resolution = new ProjectWorkspaceResolver(options).Resolve(project);

        try
        {
            Assert.True(resolution.IsValid, resolution.FailureReason);
            Assert.True(Directory.Exists(resolution.Path));
        }
        finally
        {
            if (resolution.Path is not null && Directory.Exists(resolution.Path))
            {
                Directory.Delete(resolution.Path, recursive: true);
            }
        }
    }

    /// <summary>Phase 18.15 §6 — provisioning failure must fail Project creation with no misleading success and no persisted row.</summary>
    [Fact]
    public async Task CreateAsync_WorkspaceProvisioningFails_ThrowsAndPersistsNoProjectRow()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new ProjectService(new ProjectRepository(db), new FailingWorkspaceProvisioner());

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync("Doomed Project", "desc"));

        await using var verifyDb = new FactoryDbContext(_options);
        var reloaded = await new ProjectRepository(verifyDb).GetBySlugAsync("doomed-project");
        Assert.Null(reloaded);
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
