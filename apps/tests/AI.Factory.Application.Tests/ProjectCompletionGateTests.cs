using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.10 §21 "Tests — Mandatory" — deterministic coverage for
/// IProjectCompletionGate: completion detection, dependency semantics,
/// active-AgentRun policy, lifecycle progression, Project-level verification
/// policy, and idempotency/concurrency. The full scheduler/orchestrator ->
/// Reviewing -> HumanReview end-to-end proof lives in
/// ProjectCompletionIntegrationTests.cs instead (§22).
/// </summary>
public sealed class ProjectCompletionGateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectCompletionGateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FakeVerificationGate : IVerificationGate
    {
        private readonly VerificationStatus _status;
        private readonly string _summary;

        public FakeVerificationGate(VerificationStatus status, string summary = "fake") =>
            (_status, _summary) = (status, summary);

        public int CallCount { get; private set; }

        public Task<VerificationResult> VerifyAsync(
            Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only VerifyProjectAsync is expected from ProjectCompletionGate.");

        public Task<VerificationResult> VerifyProjectAsync(
            Project project, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new VerificationResult(_status, _summary, 0, TimeSpan.Zero));
        }
    }

    private static ProjectCompletionGate CreateGate(FactoryDbContext db, IVerificationGate? verificationGate = null) =>
        new(
            new ProjectRepository(db),
            new AgentRunRepository(db),
            verificationGate ?? new FakeVerificationGate(VerificationStatus.Passed),
            NullLogger<ProjectCompletionGate>.Instance);

    private async Task<Guid> SeedProjectAtStatusAsync(ProjectStatus status, string name)
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new ProjectRepository(db);
        var project = await new ProjectService(repo).CreateAsync(name, "d");

        // Direct field assignment (Project.Status has a plain public setter,
        // unlike WorkItem.Status) — legitimate here because these tests
        // exercise the gate's reaction to a given persisted status, not the
        // ProjectTransitions guard itself (already covered by
        // ProjectTransitionsTests/ProjectServiceTests).
        project.Status = status;
        await repo.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>Creates one WorkItem and drives it through real WorkItemTransitions to the requested terminal/intermediate status.</summary>
    private async Task<Guid> SeedWorkItemAtStatusAsync(Guid projectId, string title, WorkItemStatus target)
    {
        await using var db = new FactoryDbContext(_options);
        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, title, null, "Normal");

        var path = target switch
        {
            WorkItemStatus.Draft => Array.Empty<WorkItemStatus>(),
            WorkItemStatus.Ready => [WorkItemStatus.Ready],
            WorkItemStatus.Blocked => [WorkItemStatus.Ready, WorkItemStatus.Blocked],
            WorkItemStatus.Running => [WorkItemStatus.Ready, WorkItemStatus.Running],
            WorkItemStatus.Testing => [WorkItemStatus.Ready, WorkItemStatus.Running, WorkItemStatus.Testing],
            WorkItemStatus.Repairing =>
                [WorkItemStatus.Ready, WorkItemStatus.Running, WorkItemStatus.Testing, WorkItemStatus.Repairing],
            WorkItemStatus.Completed => [WorkItemStatus.Ready, WorkItemStatus.Running, WorkItemStatus.Completed],
            WorkItemStatus.Failed => [WorkItemStatus.Ready, WorkItemStatus.Running, WorkItemStatus.Failed],
            WorkItemStatus.Cancelled => [WorkItemStatus.Ready, WorkItemStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        foreach (var status in path)
        {
            await workItems.TransitionStatusAsync(item.Id, status);
        }

        return item.Id;
    }

    private async Task<Guid> SeedAgentDefinitionAsync()
    {
        await using var db = new FactoryDbContext(_options);
        var definition = await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Agent-Implementation", AgentType = "Implementation", SystemPrompt = "x"
        });
        return definition.Id;
    }

    /// <summary>Seeds one AgentRun linked to the Project, left at the given (possibly non-terminal) status.</summary>
    private async Task SeedAgentRunAsync(Guid projectId, Guid agentDefinitionId, AgentRunStatus status)
    {
        await using var db = new FactoryDbContext(_options);
        var repo = new AgentRunRepository(db);
        var run = await repo.CreateAsync(new AgentRun { AgentDefinitionId = agentDefinitionId, ProjectId = projectId });

        var path = status switch
        {
            AgentRunStatus.Created => Array.Empty<AgentRunStatus>(),
            AgentRunStatus.Queued => [AgentRunStatus.Queued],
            AgentRunStatus.Running => [AgentRunStatus.Queued, AgentRunStatus.Running],
            AgentRunStatus.Succeeded => [AgentRunStatus.Queued, AgentRunStatus.Running, AgentRunStatus.Succeeded],
            AgentRunStatus.Failed => [AgentRunStatus.Queued, AgentRunStatus.Running, AgentRunStatus.Failed],
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        foreach (var next in path)
        {
            run.TransitionTo(next);
            await repo.UpdateAsync(run);
        }
    }

    private async Task<(Project Project, IReadOnlyList<WorkItem> Items)> LoadAsync(FactoryDbContext db, Guid projectId)
    {
        var project = await new ProjectRepository(db).GetByIdAsync(projectId);
        var items = await new WorkItemRepository(db).GetByProjectIdAsync(projectId);
        return (project!, items);
    }

    private async Task<Project> ReloadProjectAsync(Guid projectId)
    {
        await using var db = new FactoryDbContext(_options);
        return (await new ProjectRepository(db).GetByIdAsync(projectId))!;
    }

    // ---------------- Completion detection ----------------

    [Theory]
    [InlineData(WorkItemStatus.Ready)]
    [InlineData(WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Testing)]
    [InlineData(WorkItemStatus.Repairing)]
    [InlineData(WorkItemStatus.Blocked)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task ProjectWithNonCompletedWorkItem_IsNotEligible(WorkItemStatus status)
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, $"P-{status}");
        await SeedWorkItemAtStatusAsync(projectId, "A", status);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.None, result.Action);
        Assert.Equal(ProjectStatus.Implementation, project.Status);
    }

    [Fact]
    public async Task ProjectWithAllWorkItemsCompleted_IsEligibleForTesting()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-AllCompleted");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);
        await SeedWorkItemAtStatusAsync(projectId, "B", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.AdvancedToTesting, result.Action);
    }

    // ---------------- Dependency semantics ----------------

    [Fact]
    public async Task CompletedDependency_AllowsProjectCompletionEvaluation()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-DepOk");
        var aId = await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);
        var bId = await SeedWorkItemAtStatusAsync(projectId, "B", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            await new WorkItemRepository(db).AddDependencyAsync(bId, aId);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(verifyDb, projectId);
        var result = await CreateGate(verifyDb).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.AdvancedToTesting, result.Action);
    }

    [Fact]
    public async Task UncompletedDependency_PreventsProjectCompletionEvaluation()
    {
        // B depends on A; A never reached Completed, so B (still Ready, not
        // yet dispatched — mirroring real scheduler behavior) can't be
        // Completed either. The gate's "all WorkItems Completed" check
        // already implies dependency satisfaction without re-running
        // ITaskDependencyResolver (Phase 18.10 §4 — documented in
        // ProjectCompletionGate's own class doc).
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-DepBlocked");
        var aId = await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Ready);
        var bId = await SeedWorkItemAtStatusAsync(projectId, "B", WorkItemStatus.Ready);

        await using (var db = new FactoryDbContext(_options))
        {
            await new WorkItemRepository(db).AddDependencyAsync(bId, aId);
        }

        await using var verifyDb = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(verifyDb, projectId);
        var result = await CreateGate(verifyDb).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.None, result.Action);
    }

    // ---------------- Active AgentRuns ----------------

    [Fact]
    public async Task ActiveProjectAgentRun_PreventsReview()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Reviewing, "P-ActiveRun");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);
        var definitionId = await SeedAgentDefinitionAsync();
        await SeedAgentRunAsync(projectId, definitionId, AgentRunStatus.Running);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.None, result.Action);
        Assert.Equal(ProjectStatus.Reviewing, project.Status);
    }

    [Fact]
    public async Task TerminalProjectAgentRuns_DoNotBlockReview()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Reviewing, "P-TerminalRun");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);
        var definitionId = await SeedAgentDefinitionAsync();
        await SeedAgentRunAsync(projectId, definitionId, AgentRunStatus.Succeeded);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.AdvancedToHumanReview, result.Action);
    }

    // ---------------- Lifecycle ----------------

    [Fact]
    public async Task AllWorkItemsCompleted_TransitionsProjectToTesting()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-ToTesting");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            await CreateGate(db).EvaluateAsync(project, items);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.Testing, reloaded.Status);
    }

    [Fact]
    public async Task ProjectTestingPassed_TransitionsToReviewing()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-TestingPass");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.Passed))
                .EvaluateAsync(project, items);
            Assert.Equal(ProjectCompletionGateAction.AdvancedToReviewing, result.Action);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.Reviewing, reloaded.Status);
        Assert.Null(reloaded.CurrentError);
    }

    [Fact]
    public async Task ProjectReviewing_TransitionsToHumanReview()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Reviewing, "P-Reviewing");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            var result = await CreateGate(db).EvaluateAsync(project, items);
            Assert.Equal(ProjectCompletionGateAction.AdvancedToHumanReview, result.Action);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.HumanReview, reloaded.Status);
    }

    [Fact]
    public async Task ProjectCannotSkipHumanReview()
    {
        // A single EvaluateAsync call must advance at most one lifecycle
        // step: from Implementation, that is Testing — never further.
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-NoSkip");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            await CreateGate(db).EvaluateAsync(project, items);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.Testing, reloaded.Status);
    }

    [Fact]
    public async Task ProjectCannotBecomeCompletedFromAgentSuccess()
    {
        // Drive the full deterministic chain the gate owns (Implementation ->
        // Testing -> Reviewing -> HumanReview) via repeated evaluation, and
        // prove it stops there — nothing in this gate can ever produce
        // ProjectStatus.Completed.
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-NeverAutoCompletes");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        for (var i = 0; i < 4; i++)
        {
            await using var db = new FactoryDbContext(_options);
            var (project, items) = await LoadAsync(db, projectId);
            await CreateGate(db).EvaluateAsync(project, items);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.HumanReview, reloaded.Status);
        Assert.NotEqual(ProjectStatus.Completed, reloaded.Status);
    }

    // ---------------- Verification ----------------

    [Fact]
    public async Task ProjectVerificationBuildFailure_IsNotPassed()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-BuildFail");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.Failed, "Build failed"))
            .EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.Failed, result.Action);
        Assert.Equal(ProjectStatus.Failed, project.Status);
        Assert.Equal("Build failed", project.CurrentError);
    }

    [Fact]
    public async Task ProjectVerificationTestFailure_IsNotPassed()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-TestFail");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.Failed, "Tests failed"))
            .EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.Failed, result.Action);
        Assert.Equal(ProjectStatus.Failed, project.Status);
        Assert.Equal("Tests failed", project.CurrentError);
    }

    [Fact]
    public async Task ProjectVerificationTimeout_IsNotPassed()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-Timeout");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.TimedOut, "Timed out"))
            .EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.Failed, result.Action);
        Assert.Equal(ProjectStatus.Failed, project.Status);
    }

    [Fact]
    public async Task ProjectVerificationCancellation_IsNotPassed()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-Cancelled");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.Cancelled, "Cancelled"))
            .EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.Cancelled, result.Action);
        Assert.Equal(ProjectStatus.Cancelled, project.Status);
    }

    [Fact]
    public async Task ProjectVerificationSuccess_AllowsReview()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Testing, "P-Success");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db, new FakeVerificationGate(VerificationStatus.Passed))
            .EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.AdvancedToReviewing, result.Action);
        Assert.Equal(ProjectStatus.Reviewing, project.Status);
    }

    // ---------------- Idempotency / concurrency ----------------

    [Fact]
    public async Task RepeatedProjectGateEvaluation_IsIdempotent()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Reviewing, "P-Repeated");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            var first = await CreateGate(db).EvaluateAsync(project, items);
            Assert.Equal(ProjectCompletionGateAction.AdvancedToHumanReview, first.Action);
        }

        // A second, independent observation of the now-HumanReview Project
        // must be a safe no-op — never re-transition, never throw.
        await using (var db = new FactoryDbContext(_options))
        {
            var (project, items) = await LoadAsync(db, projectId);
            var second = await CreateGate(db).EvaluateAsync(project, items);
            Assert.Equal(ProjectCompletionGateAction.None, second.Action);
        }

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.HumanReview, reloaded.Status);
    }

    [Fact]
    public async Task ConcurrentProjectGateEvaluation_IsConcurrencySafe()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-Concurrent");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var dbA = new FactoryDbContext(_options);
        await using var dbB = new FactoryDbContext(_options);

        var (projectA, itemsA) = await LoadAsync(dbA, projectId);
        var (projectB, itemsB) = await LoadAsync(dbB, projectId);

        var results = await Task.WhenAll(
            CreateGate(dbA).EvaluateAsync(projectA, itemsA),
            CreateGate(dbB).EvaluateAsync(projectB, itemsB));

        Assert.Single(results, x => x.Action == ProjectCompletionGateAction.AdvancedToTesting);
        Assert.Single(results, x => x.Action == ProjectCompletionGateAction.None);

        var reloaded = await ReloadProjectAsync(projectId);
        Assert.Equal(ProjectStatus.Testing, reloaded.Status);
    }

    [Fact]
    public async Task AlreadyTerminalProject_IsNotOverwritten()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Completed, "P-Terminal");
        await SeedWorkItemAtStatusAsync(projectId, "A", WorkItemStatus.Completed);

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.None, result.Action);
        Assert.Equal(ProjectStatus.Completed, project.Status);
    }

    // ---------------- Empty project ----------------

    [Fact]
    public async Task EmptyProject_UsesExplicitFailClosedPolicy()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.Implementation, "P-Empty");

        await using var db = new FactoryDbContext(_options);
        var (project, items) = await LoadAsync(db, projectId);
        Assert.Empty(items);

        var result = await CreateGate(db).EvaluateAsync(project, items);

        Assert.Equal(ProjectCompletionGateAction.None, result.Action);
        Assert.Equal(ProjectStatus.Implementation, project.Status);
    }
}
