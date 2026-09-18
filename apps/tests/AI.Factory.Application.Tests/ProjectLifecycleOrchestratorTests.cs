using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.25 §17 — deterministic coverage of the Project lifecycle
/// authority: start validation, start idempotency/concurrency, automatic
/// state progression, the Planner boundary and its fail-closed semantics,
/// restart/resume, and the HumanReview stop.
///
/// Every test drives the real <see cref="ProjectLifecycleOrchestrator"/>
/// against a real SQLite-backed <see cref="ProjectRepository"/>, so the
/// transition validation, optimistic concurrency and persistence under test
/// are the production ones. Only the Planner and plan-read collaborators are
/// faked — they are already proven by Phase 18.23/18.24, and running a real
/// LLM has no place in a deterministic suite.
/// </summary>
public sealed class ProjectLifecycleOrchestratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public ProjectLifecycleOrchestratorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------- Start ----------------

    [Fact]
    public async Task StartAsync_InitializedProject_RecordsTheDurableLifecycleMarker()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);

        var started = await CreateOrchestrator(db).StartAsync(projectId, "operator@factory.local");

        Assert.NotNull(started.LifecycleStartedAtUtc);
        Assert.Equal(ProjectStatus.Analyzing, started.Status);

        await using var verify = new FactoryDbContext(_options);
        Assert.NotNull((await new ProjectRepository(verify).GetByIdAsync(projectId))!.LifecycleStartedAtUtc);
    }

    [Fact]
    public async Task StartAsync_UninitializedProject_IsRejectedAndNeverStarted()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Created);

        await Assert.ThrowsAsync<ConflictException>(() => CreateOrchestrator(db).StartAsync(projectId));

        await using var verify = new FactoryDbContext(_options);
        Assert.Null((await new ProjectRepository(verify).GetByIdAsync(projectId))!.LifecycleStartedAtUtc);
    }

    [Theory]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Cancelled)]
    [InlineData(ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Implementation)]
    public async Task StartAsync_NonStartableStatus_IsRejected(ProjectStatus status)
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, status);

        await Assert.ThrowsAsync<ConflictException>(() => CreateOrchestrator(db).StartAsync(projectId));
    }

    [Fact]
    public async Task StartAsync_AlreadyStartedProject_IsIdempotentAndKeepsTheOriginalStartTimestamp()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);

        var first = await CreateOrchestrator(db).StartAsync(projectId);
        var firstStartedAt = first.LifecycleStartedAtUtc;

        await using var second = new FactoryDbContext(_options);
        var again = await CreateOrchestrator(second).StartAsync(projectId);

        Assert.Equal(firstStartedAt, again.LifecycleStartedAtUtc);
    }

    [Fact]
    public async Task StartAsync_AlreadyRunningLifecycle_IsIdempotentEvenPastTheStartableStatus()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);
        var orchestrator = CreateOrchestrator(db);

        var started = await orchestrator.StartAsync(projectId);
        await orchestrator.AdvanceAsync(projectId);

        await using var second = new FactoryDbContext(_options);
        var again = await CreateOrchestrator(second).StartAsync(projectId);

        Assert.Equal(started.LifecycleStartedAtUtc, again.LifecycleStartedAtUtc);
        Assert.Equal(ProjectStatus.Specification, again.Status);
    }

    [Fact]
    public async Task StartAsync_MissingDescription_IsRejected()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing, description: "   ");

        await Assert.ThrowsAsync<ValidationException>(() => CreateOrchestrator(db).StartAsync(projectId));
    }

    [Fact]
    public async Task StartAsync_UnavailableWorkspace_IsRejectedAndNeverStarted()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);

        await Assert.ThrowsAsync<IOException>(() =>
            CreateOrchestrator(db, provisioner: new FailingWorkspaceProvisioner()).StartAsync(projectId));

        await using var verify = new FactoryDbContext(_options);
        Assert.Null((await new ProjectRepository(verify).GetByIdAsync(projectId))!.LifecycleStartedAtUtc);
    }

    [Fact]
    public async Task StartAsync_UnknownProject_ThrowsNotFound()
    {
        await using var db = new FactoryDbContext(_options);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateOrchestrator(db).StartAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// §7 — two genuinely concurrent starts against separate DbContexts (and
    /// therefore separate change trackers) resolve to exactly one
    /// authoritative start: both callers observe the same persisted
    /// LifecycleStartedAtUtc, and the Project's lifecycle is started once.
    /// </summary>
    [Fact]
    public async Task StartAsync_TwoConcurrentStarts_ProduceExactlyOneAuthoritativeStart()
    {
        Guid projectId;

        await using (var seed = new FactoryDbContext(_options))
        {
            projectId = await SeedAsync(seed, ProjectStatus.Analyzing);
        }

        await using var dbA = new FactoryDbContext(_options);
        await using var dbB = new FactoryDbContext(_options);

        var resultA = await CreateOrchestrator(dbA).StartAsync(projectId, "a");
        var resultB = await CreateOrchestrator(dbB).StartAsync(projectId, "b");

        await using var verify = new FactoryDbContext(_options);
        var persisted = (await new ProjectRepository(verify).GetByIdAsync(projectId))!;

        Assert.NotNull(persisted.LifecycleStartedAtUtc);
        Assert.Equal(persisted.LifecycleStartedAtUtc, resultA.LifecycleStartedAtUtc);
        Assert.Equal(persisted.LifecycleStartedAtUtc, resultB.LifecycleStartedAtUtc);
    }

    // ---------------- Automatic state progression ----------------

    [Fact]
    public async Task AdvanceAsync_StartedProject_WalksTheValidLifecycleOneStageAtATime()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);
        var planner = new FakeProjectPlanner();
        var orchestrator = CreateOrchestrator(db, planner: planner);

        await orchestrator.StartAsync(projectId);

        Assert.Equal(ProjectStatus.Specification, (await orchestrator.AdvanceAsync(projectId)).Status);
        Assert.Equal(ProjectStatus.Architecture, (await orchestrator.AdvanceAsync(projectId)).Status);
        Assert.Equal(ProjectStatus.Planning, (await orchestrator.AdvanceAsync(projectId)).Status);

        // Planning dispatches the real Planner path and waits — it never
        // jumps straight to Implementation.
        var planning = await orchestrator.AdvanceAsync(projectId);
        Assert.Equal(ProjectLifecycleAction.PlannerStarted, planning.Action);
        Assert.Equal(ProjectStatus.Planning, planning.Status);
    }

    [Fact]
    public async Task AdvanceAsync_ProjectThatWasNeverStarted_IsAlwaysANoOp()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);

        var result = await CreateOrchestrator(db).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.None, result.Action);
        Assert.Equal(ProjectStatus.Analyzing, result.Status);
    }

    [Theory]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Cancelled)]
    public async Task AdvanceAsync_TerminalProject_IsANoOp(ProjectStatus status)
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, status, lifecycleStarted: true);

        var result = await CreateOrchestrator(db).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.None, result.Action);
        Assert.Equal(status, result.Status);
    }

    /// <summary>
    /// §13/§20 — the autonomous lifecycle stops at HumanReview and stays
    /// stationary. It cannot produce Completed: no code path in the
    /// orchestrator transitions to it, and Advance is a no-op there.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_ProjectInHumanReview_NeverCompletesIt()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.HumanReview, lifecycleStarted: true);
        var orchestrator = CreateOrchestrator(db);

        for (var i = 0; i < 5; i++)
        {
            var result = await orchestrator.AdvanceAsync(projectId);
            Assert.Equal(ProjectLifecycleAction.None, result.Action);
        }

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(
            ProjectStatus.HumanReview, (await new ProjectRepository(verify).GetByIdAsync(projectId))!.Status);
    }

    /// <summary>
    /// §5/§13 — Implementation onwards belongs to IProjectCompletionGate. The
    /// lifecycle owner must not touch those stages, or two authorities would
    /// be writing the same Project status.
    /// </summary>
    [Theory]
    [InlineData(ProjectStatus.Implementation)]
    [InlineData(ProjectStatus.Testing)]
    [InlineData(ProjectStatus.Reviewing)]
    public async Task AdvanceAsync_CompletionGateStages_AreNotOwnedByTheLifecycleOrchestrator(ProjectStatus status)
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, status, lifecycleStarted: true);

        var result = await CreateOrchestrator(db).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.None, result.Action);
        Assert.Equal(status, result.Status);
    }

    // ---------------- Planner boundary ----------------

    [Fact]
    public async Task AdvanceAsync_PlanningWithAPersistedPlan_AdvancesToImplementation()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var result = await CreateOrchestrator(db, planService: new FakePlanService(PlanFor(projectId)))
            .AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.Advanced, result.Action);
        Assert.Equal(ProjectStatus.Implementation, result.Status);
    }

    [Fact]
    public async Task AdvanceAsync_PlanningWithAnInFlightPlanner_WaitsAndNeverDispatchesASecondPlanner()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner { LatestRun = RunWith(AgentRunStatus.Running) };
        var orchestrator = CreateOrchestrator(db, planner: planner);

        for (var i = 0; i < 3; i++)
        {
            var result = await orchestrator.AdvanceAsync(projectId);
            Assert.Equal(ProjectLifecycleAction.Waiting, result.Action);
        }

        Assert.Equal(0, planner.GenerateCalls);
    }

    [Fact]
    public async Task AdvanceAsync_PlanningDispatchesThePlannerExactlyOnce()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner();
        var orchestrator = CreateOrchestrator(db, planner: planner);

        await orchestrator.AdvanceAsync(projectId);
        await orchestrator.AdvanceAsync(projectId);
        await orchestrator.AdvanceAsync(projectId);

        Assert.Equal(1, planner.GenerateCalls);
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut)]
    public async Task AdvanceAsync_PlannerRunFailed_FailsTheProjectClosedWithADiagnostic(AgentRunStatus status)
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner { LatestRun = RunWith(status) };
        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.Failed, result.Action);
        Assert.Equal(ProjectStatus.Failed, result.Status);

        await using var verify = new FactoryDbContext(_options);
        var persisted = (await new ProjectRepository(verify).GetByIdAsync(projectId))!;

        Assert.Equal(ProjectStatus.Failed, persisted.Status);
        Assert.Contains(status.ToString(), persisted.CurrentError);
    }

    [Fact]
    public async Task AdvanceAsync_InvalidPlan_NeverReachesImplementation()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner
        {
            LatestRun = RunWith(AgentRunStatus.Succeeded),
            Materialization = new PlannerMaterializationResult(
                PlannerMaterializationOutcome.PlanValidationFailed, null, "no acceptance criteria")
        };

        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.Failed, result.Action);

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(ProjectStatus.Failed, (await new ProjectRepository(verify).GetByIdAsync(projectId))!.Status);
    }

    /// <summary>
    /// §8 — the crash window: the Planner AgentRun succeeded but the Worker
    /// died before AgentRunWorker could materialize it. On restart the
    /// lifecycle owner re-drives the same idempotent materialization rather
    /// than starting a second Planner, and the Project resumes.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_SucceededPlannerRunNotYetMaterialized_MaterializesAndResumes()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner
        {
            LatestRun = RunWith(AgentRunStatus.Succeeded),
            Materialization = new PlannerMaterializationResult(
                PlannerMaterializationOutcome.Succeeded, PlanFor(projectId), null)
        };

        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectStatus.Implementation, result.Status);
        Assert.Equal(1, planner.MaterializeCalls);
        Assert.Equal(0, planner.GenerateCalls);
    }

    /// <summary>
    /// §8 — restart safety, end to end: a brand new orchestrator instance over
    /// a brand new DbContext (i.e. a restarted Worker) resumes purely from the
    /// persisted Project row, reaches Implementation, and never dispatches a
    /// second Planner or re-runs a completed stage.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_AfterSimulatedWorkerRestart_ResumesFromPersistedStateWithoutDuplicating()
    {
        Guid projectId;
        var planner = new FakeProjectPlanner();

        await using (var db = new FactoryDbContext(_options))
        {
            projectId = await SeedAsync(db, ProjectStatus.Analyzing);
            var orchestrator = CreateOrchestrator(db, planner: planner);

            await orchestrator.StartAsync(projectId);
            await orchestrator.AdvanceAsync(projectId); // -> Specification
            await orchestrator.AdvanceAsync(projectId); // -> Architecture
        }

        // "Restart": nothing in memory survives; only the Project row does.
        await using (var db = new FactoryDbContext(_options))
        {
            var orchestrator = CreateOrchestrator(db, planner: planner);

            Assert.Equal(ProjectStatus.Planning, (await orchestrator.AdvanceAsync(projectId)).Status);
            Assert.Equal(ProjectLifecycleAction.PlannerStarted, (await orchestrator.AdvanceAsync(projectId)).Action);

            planner.LatestRun = RunWith(AgentRunStatus.Succeeded);
            planner.Materialization = new PlannerMaterializationResult(
                PlannerMaterializationOutcome.Succeeded, PlanFor(projectId), null);

            Assert.Equal(ProjectStatus.Implementation, (await orchestrator.AdvanceAsync(projectId)).Status);
        }

        Assert.Equal(1, planner.GenerateCalls);
    }

    /// <summary>
    /// §6/§20 — the orchestrator never assigns Project.Status, so it can never
    /// produce a transition the domain table forbids. Proven behaviourally:
    /// every persisted status the lifecycle produced is reachable from its
    /// predecessor under ProjectTransitions.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_EveryProducedTransitionIsValidUnderTheDomainTransitionTable()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Analyzing);

        var planner = new FakeProjectPlanner();
        var planService = new FakePlanService(null);
        var orchestrator = CreateOrchestrator(db, planner: planner, planService: planService);

        await orchestrator.StartAsync(projectId);

        var observed = new List<ProjectStatus> { ProjectStatus.Analyzing };

        for (var i = 0; i < 6; i++)
        {
            if (i == 4)
            {
                planService.Plan = PlanFor(projectId);
            }

            var result = await orchestrator.AdvanceAsync(projectId);

            if (observed[^1] != result.Status)
            {
                observed.Add(result.Status);
            }
        }

        Assert.Equal(
            new[]
            {
                ProjectStatus.Analyzing, ProjectStatus.Specification, ProjectStatus.Architecture,
                ProjectStatus.Planning, ProjectStatus.Implementation
            },
            observed);

        for (var i = 1; i < observed.Count; i++)
        {
            Assert.True(
                ProjectTransitions.IsValid(observed[i - 1], observed[i]),
                $"{observed[i - 1]} -> {observed[i]} is not a valid Project transition.");
        }
    }

    // ---------------- Phase 18.26 §6/§7 — Planner crash recovery ----------------

    /// <summary>
    /// A Worker that died mid-Planner is not a Planner failure. WorkItem-backed
    /// runs get a second chance from the WorkItem repair path; a Planner run
    /// has no such layer, so without this a routine restart would permanently
    /// fail a healthy Project.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_PlannerOrphanedByWorkerCrash_RedispatchesOnce_WithoutFailingTheProject()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner { LatestRun = OrphanedRun() };
        planner.Runs = new[] { planner.LatestRun! };

        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.PlannerStarted, result.Action);
        Assert.Equal(ProjectStatus.Planning, result.Status);
        Assert.Equal(1, planner.GenerateCalls);

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(ProjectStatus.Planning, (await new ProjectRepository(verify).GetByIdAsync(projectId))!.Status);
    }

    /// <summary>The retry is bounded: a second crash in a row fails closed rather than looping on provider credit.</summary>
    [Fact]
    public async Task AdvanceAsync_PlannerOrphanedTwice_FailsClosed()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var planner = new FakeProjectPlanner { LatestRun = OrphanedRun() };
        planner.Runs = new[] { planner.LatestRun!, OrphanedRun() };

        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.Failed, result.Action);
        Assert.Equal(0, planner.GenerateCalls);

        await using var verify = new FactoryDbContext(_options);
        Assert.Equal(ProjectStatus.Failed, (await new ProjectRepository(verify).GetByIdAsync(projectId))!.Status);
    }

    /// <summary>A Planner that genuinely failed is still fail-closed — the retry is crash-scoped, not a general retry.</summary>
    [Fact]
    public async Task AdvanceAsync_PlannerFailedOnItsOwn_IsNotRetried()
    {
        await using var db = new FactoryDbContext(_options);
        var projectId = await SeedAsync(db, ProjectStatus.Planning, lifecycleStarted: true);

        var failedRun = RunWith(AgentRunStatus.Failed);
        failedRun.ErrorMessage = "the agent could not produce a plan";

        var planner = new FakeProjectPlanner { LatestRun = failedRun };
        planner.Runs = new[] { failedRun };

        var result = await CreateOrchestrator(db, planner: planner).AdvanceAsync(projectId);

        Assert.Equal(ProjectLifecycleAction.Failed, result.Action);
        Assert.Equal(0, planner.GenerateCalls);
    }

    private static AgentRun OrphanedRun()
    {
        var run = RunWith(AgentRunStatus.Failed);
        run.ErrorMessage = $"{AgentRunRecovery.OrphanedMarker}: the Worker process restarted while this run was executing.";
        run.FailureCategory = FailureCategory.Infrastructure;
        return run;
    }

    // ---------------- Helpers ----------------

    private static ProjectLifecycleOrchestrator CreateOrchestrator(
        FactoryDbContext db,
        IProjectWorkspaceProvisioner? provisioner = null,
        IProjectPlanner? planner = null,
        IProjectPlanService? planService = null) =>
        new(
            new ProjectRepository(db),
            provisioner ?? new SucceedingWorkspaceProvisioner(),
            planner ?? new FakeProjectPlanner(),
            planService ?? new FakePlanService(null),
            NullAuditLogger.Instance,
            NullLogger<ProjectLifecycleOrchestrator>.Instance);

    private static async Task<Guid> SeedAsync(
        FactoryDbContext db,
        ProjectStatus status,
        string? description = "a real project goal",
        bool lifecycleStarted = false)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Lifecycle " + Guid.NewGuid().ToString("N")[..8],
            Slug = "lifecycle-" + Guid.NewGuid().ToString("N")[..8],
            Description = description,
            Status = status,
            RootPath = "projects/lifecycle",
            LifecycleStartedAtUtc = lifecycleStarted ? DateTime.UtcNow : null
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static ProjectPlanResult PlanFor(Guid projectId) =>
        new(projectId, "HASH", new[]
        {
            new ProjectPlanTaskResult(
                "T01", Guid.NewGuid(), "Task", WorkItemType.Implementation, "Normal",
                Array.Empty<string>(), nameof(WorkItemStatus.Ready), TaskReadinessStatus.Ready)
        });

    private static AgentRun RunWith(AgentRunStatus status) =>
        new() { Id = Guid.NewGuid(), Status = status };

    private sealed class SucceedingWorkspaceProvisioner : IProjectWorkspaceProvisioner
    {
        public ProvisioningResult Provision(Project project) => ProvisioningResult.Succeeded(project.RootPath);
    }

    private sealed class FailingWorkspaceProvisioner : IProjectWorkspaceProvisioner
    {
        public ProvisioningResult Provision(Project project) => ProvisioningResult.Failed("workspace missing");
    }

    private sealed class FakePlanService : IProjectPlanService
    {
        public FakePlanService(ProjectPlanResult? plan) => Plan = plan;

        public ProjectPlanResult? Plan { get; set; }

        public Task<ProjectPlanResult> CreatePlanAsync(
            ProjectPlanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The lifecycle orchestrator must never create a plan itself.");

        public Task<ProjectPlanResult?> GetPlanAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Plan);
    }

    private sealed class FakeProjectPlanner : IProjectPlanner
    {
        public int GenerateCalls { get; private set; }

        public int MaterializeCalls { get; private set; }

        public AgentRun? LatestRun { get; set; }

        public PlannerMaterializationResult Materialization { get; set; } =
            PlannerMaterializationResult.Skip("not configured");

        public Task<AgentRun> GenerateAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            LatestRun ??= RunWith(AgentRunStatus.Queued);
            return Task.FromResult(LatestRun);
        }

        public Task<AgentRun?> GetLatestRunAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(LatestRun);

        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(
            Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(
                Runs ?? (LatestRun is null ? Array.Empty<AgentRun>() : new[] { LatestRun }));

        /// <summary>Overrides the run history when a test needs more than "the latest run".</summary>
        public IReadOnlyList<AgentRun>? Runs { get; set; }

        public Task<PlannerMaterializationResult> MaterializeAsync(
            Guid agentRunId, CancellationToken cancellationToken = default)
        {
            MaterializeCalls++;
            return Task.FromResult(Materialization);
        }
    }
}
