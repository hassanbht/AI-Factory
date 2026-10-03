using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;
using AI.Factory.TestSupport;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.23 §10/§11/§13 — the real Planner, live.
///
/// Phase 18.22 proved every stage downstream of the plan but left the plan
/// itself operator-written. These tests close that gap: the WorkItems the
/// execution pipeline consumes here are produced by a real Planner Agent
/// running in a real Hermes container against a real model, mapped by the real
/// <see cref="PlannerResultMapper"/> and persisted by the real
/// <see cref="IProjectPlanService"/>. Nothing is injected by hand.
///
/// Opt-in only, like every other real-Hermes test:
/// <code>
/// AI_FACTORY_HERMES_SMOKE_TEST=true OMNIROUTE_API_KEY=... \
///   dotnet test --filter FullyQualifiedName~LivePlannerE2ETests
/// </code>
/// </summary>
[Collection(TestCollections.DockerAndBuilds)]
public sealed class LivePlannerE2ETests
{
    private readonly ITestOutputHelper _output;

    public LivePlannerE2ETests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Phase 18.24 §5/§9/§25 — the qualification fixture: small, deterministic,
    /// file-oriented, dependency-aware, and carrying a repair requirement that
    /// exists by <em>construction</em> rather than by hoping an agent slips.
    ///
    /// The seed repository (<see cref="LiveRepairFixture"/>) pins the current
    /// greeting in <c>tests/GreetingServiceTests.cs</c>. The moment the first
    /// task is carried out as written, that test necessarily fails, the
    /// Factory's own build/test gate reports it, and the WorkItem enters
    /// Repairing through the ordinary pipeline. Nothing in the Factory is
    /// modified to manufacture the failure (§9), and the test file is never
    /// mentioned here — so the implementation agent is not steered into fixing
    /// it pre-emptively, while the repair agent, which is handed the gate's own
    /// failure summary, can.
    ///
    /// The second task genuinely cannot be done first: it records the greeting
    /// the service actually returns.
    /// </summary>
    private const string PlannerProjectDescription =
        "This repository is a small .NET solution (Demo.slnx) whose only source file is src/GreetingService.cs, " +
        "containing a GreetingService class whose GetGreeting() method currently returns the string Hello. " +
        "Two changes are required, in this order. " +
        "First, src/GreetingService.cs must be edited so that GetGreeting() returns exactly: Hello, AI Factory! " +
        "Second, a new file RELEASE_NOTES.md must be created at the repository root, and its entire content " +
        "must be exactly the single line: Hello, AI Factory! " +
        "RELEASE_NOTES.md records the greeting the service actually returns, so it must not be written before " +
        "src/GreetingService.cs already returns the new greeting. " +
        "Do not create or delete any other file.";

    /// <summary>Outcome of one live Planner attempt — the §23 reliability table, one row per run.</summary>
    private sealed record PlannerAttempt(
        int Run,
        Guid ProjectId,
        Guid AgentRunId,
        AgentRunStatus HermesStatus,
        string StructuredResult,
        bool PlanValid,
        bool CriteriaValid,
        bool Persisted,
        IReadOnlyList<string> PlanKeys,
        string DependencyGraph,
        string? FailureStage,
        TimeSpan Duration);

    // ────────────────────────────────────────────────────────────────────────
    // §10 / §13 — real Planner smoke test, repeated
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §13 — three SUCCESSFUL independent Planner executions against the same
    /// fixture, with every attempt (successful or not) reported. "Successful"
    /// means the Factory accepted and persisted the plan through its normal
    /// validation — never that the model produced identical prose.
    /// </summary>
    [LiveFact("AI_FACTORY_HERMES_SMOKE_TEST", RequiresDocker = true)]
    public async Task RealPlanner_ProducesValidPersistedPlan_AcrossThreeSuccessfulRuns()
    {
        const int requiredSuccesses = 3;
        const int maxAttempts = 5;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var cancellationToken = cts.Token;

        using var live = LiveWorkerHost.Create();

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(
                scope.ServiceProvider.GetRequiredService<FactoryDbContext>(), cancellationToken);
        }

        var worker = live.Host.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();

        // Phase 18.25 §19 — the real production sweep drives both the lifecycle
        // orchestrator and the dispatch scheduler. Running it (rather than
        // pumping cycles from the test) is what makes "the lifecycle owner
        // produced these transitions" an observation rather than an inference.
        var scheduler = live.Host.Services.GetServices<IHostedService>()
            .OfType<TaskSchedulerHostedService>().Single();

        await worker.StartAsync(cancellationToken);
        await scheduler.StartAsync(cancellationToken);

        var attempts = new List<PlannerAttempt>();

        try
        {
            for (var run = 1; run <= maxAttempts && attempts.Count(a => a.Persisted) < requiredSuccesses; run++)
            {
                attempts.Add(await RunPlannerOnceAsync(live, run, cancellationToken));
            }
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }

        WriteReliabilityTable(attempts);

        var successes = attempts.Count(a => a.Persisted);

        Assert.True(
            successes >= requiredSuccesses,
            $"Only {successes}/{attempts.Count} live Planner attempts produced a plan the Factory accepted and persisted.");

        // §21.B — a persisted plan is not enough: every task it created must
        // carry a contract the acceptance verifier can actually read.
        foreach (var attempt in attempts.Where(a => a.Persisted))
        {
            await AssertEveryWorkItemHasReadableCriteriaAsync(live, attempt.ProjectId, cancellationToken);
        }
    }

    /// <summary>
    /// §10.8 — invalid Planner output must persist nothing. Proven against the
    /// real production path rather than a mocked one: a real terminal AgentRun
    /// carrying output that is not a structured result is materialized by the
    /// real <see cref="IProjectPlanner"/>, and the Project is left with no
    /// WorkItems at all.
    /// </summary>
    [LiveFact("AI_FACTORY_HERMES_SMOKE_TEST", RequiresDocker = true)]
    public async Task InvalidPlannerOutput_PersistsNothing_ThroughTheRealMaterializationPath()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;

        using var live = LiveWorkerHost.Create();

        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db, cancellationToken);

        var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var project = await projectService.CreateAsync("Invalid Planner Output", "d", cancellationToken);
        await MoveProjectToPlanningAsync(projectService, project.Id, cancellationToken);

        var plannerDefinition = (await new AgentDefinitionRepository(db).GetAllAsync(cancellationToken))
            .First(d => string.Equals(d.AgentType, ProjectPlanner.PlannerAgentType, StringComparison.OrdinalIgnoreCase));

        // A terminal, genuinely Succeeded Planner AgentRun whose agent answered
        // in prose — the single most common real failure mode observed live.
        var run = new AgentRun
        {
            AgentDefinitionId = plannerDefinition.Id,
            ProjectId = project.Id,
            Input = "plan it",
            Status = AgentRunStatus.Succeeded,
            Output = "Sure! I would start by adding a README and then writing some tests.",
            StructuredResult = null
        };

        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var planner = scope.ServiceProvider.GetRequiredService<IProjectPlanner>();
        var result = await planner.MaterializeAsync(run.Id, cancellationToken);

        Assert.Equal(PlannerMaterializationOutcome.CandidateInvalid, result.Outcome);
        Assert.Empty(await new WorkItemRepository(db).GetByProjectIdAsync(project.Id, cancellationToken));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IProjectPlanService>()
            .GetPlanAsync(project.Id, cancellationToken));

        _output.WriteLine($"Invalid Planner output rejected: {result.Message}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Phase 18.24 §8 — the single-run Golden Lifecycle
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 18.24 §8/§23/§26 and Phase 18.25 §18/§19/§20 — the one run the
    /// whole phase exists to observe.
    ///
    /// Everything below happens in a single uninterrupted execution against one
    /// provider/model: autonomous lifecycle progression from Analyzing to
    /// Planning, a real Planner AgentRun the Factory dispatched itself, the
    /// WorkItems it generated (none is created by this test — 18.24 §21), a
    /// deterministic acceptance failure and a real repair on the first task, a
    /// dependent task the scheduler may only release afterwards, real project
    /// verification, and an explicit human approval.
    ///
    /// Phase 18.25 §20 — this test issues NO intermediate Project status
    /// transition. Its only Project-level lifecycle actions are: create,
    /// initialize source, one IProjectLifecycleOrchestrator.StartAsync, and
    /// ProjectService.ApproveAsync at the end. Everything in between is
    /// produced by the real Worker loops (TaskSchedulerHostedService +
    /// AgentRunWorker) and proven from the persisted audit trail.
    /// </summary>
    [LiveFact("AI_FACTORY_HERMES_SMOKE_TEST", RequiresDocker = true)]
    public async Task AutonomousStart_RealPlanner_ForcedRepair_DependentDispatch_ProjectVerification_HumanApproval()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(120));
        var cancellationToken = cts.Token;

        using var live = LiveWorkerHost.Create();
        _output.WriteLine($"Provider base URL class: {DescribeBaseUrl(LiveWorkerHost.LiveBaseUrl)}");
        _output.WriteLine($"Model: {LiveWorkerHost.LiveModel}");
        _output.WriteLine($"Workspace base: {live.ProjectsBaseDirectory}");

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(
                scope.ServiceProvider.GetRequiredService<FactoryDbContext>(), cancellationToken);
        }

        var worker = live.Host.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();
        // Phase 18.25 §19 — the real production sweep drives both the lifecycle
        // orchestrator and the dispatch scheduler, so every intermediate Project
        // transition below is produced by the lifecycle owner rather than by
        // this test pumping cycles by hand.
        var scheduler = live.Host.Services.GetServices<IHostedService>()
            .OfType<TaskSchedulerHostedService>().Single();

        await worker.StartAsync(cancellationToken);
        await scheduler.StartAsync(cancellationToken);

        Guid projectId;
        IReadOnlyList<ProjectPlanTaskResult> planTasks;
        IReadOnlyList<WorkItemObservation> observations;

        try
        {
            var attempt = await RunPlannerOnceAsync(live, run: 1, cancellationToken);
            WriteReliabilityTable(new[] { attempt });

            Assert.True(attempt.Persisted, $"The live Planner produced no usable plan: {attempt.FailureStage}.");

            projectId = attempt.ProjectId;

            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                planTasks = (await scope.ServiceProvider.GetRequiredService<IProjectPlanService>()
                    .GetPlanAsync(projectId, cancellationToken))!.Tasks;
            }

            // §21 — the plan must contain a real prerequisite/dependent pair, or
            // the dependency leg of §11 cannot be observed at all.
            Assert.True(
                planTasks.Any(t => t.DependsOn.Count > 0),
                "The Planner produced no dependent task, so dependency dispatch cannot be observed in this run.");

            // §10/§21 — the persisted graph must be exactly what the plan
            // declared: no dependency silently dropped, none invented.
            await AssertPersistedGraphMatchesPlanAsync(live, projectId, planTasks, cancellationToken);
            await AssertEveryWorkItemHasReadableCriteriaAsync(live, projectId, cancellationToken);

            // Phase 18.25 §18/§20 — Planning -> Implementation is produced by
            // the lifecycle orchestrator once the plan is persisted, never by
            // this test.
            await WaitForProjectStatusAsync(
                live, projectId, TimeSpan.FromMinutes(3), ProjectStatus.Implementation);

            observations = await DriveGeneratedWorkItemsAsync(
                live, projectId, planTasks, TimeSpan.FromMinutes(60), cancellationToken);

            await ReportAgentRunsAsync(live, projectId, cancellationToken);

            // §9/§12 — at least one Planner-generated WorkItem must have gone
        // through the real Repairing path. RepairAttemptCount is incremented
        // only inside TaskDispatchScheduler.DispatchRepairAsync, whose sole
        // entry condition is a WorkItem already in Repairing — which in turn is
        // only reachable from Testing on a VerificationGate.Failed result. A
        // non-zero count is therefore proof of a real repair, not of a retry.
            var repaired = observations.Where(o => o.RepairAttemptCount > 0).ToList();

            Assert.True(
                repaired.Count > 0,
                "No Planner-generated WorkItem entered the repair path, so the §9 repair leg is unobserved in this run.");

            foreach (var o in repaired)
            {
                _output.WriteLine(
                    $"REPAIR {o.PlanKey} WorkItem={o.WorkItemId} RepairAttemptCount={o.RepairAttemptCount} " +
                    $"timeline={string.Join(" -> ", o.StatusTimeline)}");
            }

            // §11 — dependency semantics, from the database rather than from the
            // polling loop alone: a dependent task's first AgentRun may not have
            // started before its prerequisite reached Completed.
            await AssertDependentRunsStartedAfterPrerequisitesAsync(live, projectId, planTasks, cancellationToken);

            // §13 — Implementation -> Testing -> project verification -> Reviewing
            // -> HumanReview, driven purely by real scheduler cycles. No Project
            // status is set by hand.
            //
            // Phase 18.25 — this wait, and everything below it, MUST stay inside
            // the try: the real TaskSchedulerHostedService is what drives the
            // completion gate now, so stopping it before this point leaves the
            // Project parked in Implementation forever.
            var humanReview = await WaitForProjectStatusAsync(
                live, projectId, TimeSpan.FromMinutes(20), ProjectStatus.HumanReview);
            Assert.Equal(ProjectStatus.HumanReview, humanReview.Status);

            // Phase 18.25 §19 — the intermediate transitions must be shown to have
            // been produced by the lifecycle owner, from the persisted audit trail
            // rather than inferred from the final status.
            await AssertLifecycleEvidenceAsync(live, projectId, cancellationToken);

            // Phase D — persisted execution evidence (adapter metadata,
            // artifacts, verification, review), read back before the approval.
            await LiveExecutionEvidence.ReportAsync(live, projectId, _output, cancellationToken);

            // §13.6/§13.7 — the generic transition stays refused; approval is the
            // one manual step and the only way out.
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();

                await Assert.ThrowsAsync<ConflictException>(() =>
                    projectService.TransitionStatusAsync(projectId, ProjectStatus.Completed, cancellationToken));

                var completed = await projectService.ApproveAsync(projectId, "qa-lead@factory.local", cancellationToken);
                Assert.Equal(ProjectStatus.Completed, completed.Status);
            }

            _output.WriteLine($"Project {projectId} Completed from a Planner-generated plan, after explicit human approval.");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One complete live Planner execution through the production path:
    /// Project -> workspace -> real Git source -> ONE
    /// IProjectLifecycleOrchestrator.StartAsync -> autonomous
    /// Analyzing/Specification/Architecture/Planning progression ->
    /// Factory-dispatched Planner AgentRun -> AgentRunWorker -> Hermes ->
    /// model -> StructuredAgentResultParser -> PlannerResultMapper ->
    /// ProjectPlanService -> persisted WorkItems.
    ///
    /// Phase 18.25 §20 — no intermediate Project status transition is issued
    /// here. Nothing reaches into Planner internals (18.23 §10).
    /// </summary>
    private async Task<PlannerAttempt> RunPlannerOnceAsync(
        LiveWorkerHost live, int run, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Guid projectId;

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();

            var project = await projectService.CreateAsync(
                $"Live Planner Demo {run}", PlannerProjectDescription, cancellationToken);
            projectId = project.Id;

            await projectService.InitializeSourceAsync(
                projectId,
                new SourceSpecification(ProjectSourceType.GitRepository, live.BareRepoPath, "main"),
                cancellationToken);

            // §18/§20 — the ONE lifecycle action this test performs. Every
            // Project transition after this point is the Factory's own.
            await scope.ServiceProvider.GetRequiredService<IProjectLifecycleOrchestrator>()
                .StartAsync(projectId, "qa-lead@factory.local", cancellationToken);
        }

        // The lifecycle owner walks Analyzing -> Specification -> Architecture
        // -> Planning and dispatches the Planner itself; the test only watches.
        await WaitForProjectStatusAsync(live, projectId, TimeSpan.FromMinutes(5), ProjectStatus.Planning);

        var agentRunId = (await WaitForPlannerRunAsync(
            live, projectId, TimeSpan.FromMinutes(5), cancellationToken)).Id;

        _output.WriteLine($"[run {run}] ProjectId={projectId} Planner AgentRunId={agentRunId} (dispatched by the lifecycle owner)");

        var terminalRun = await WaitForAgentRunTerminalAsync(live, agentRunId, TimeSpan.FromMinutes(12), cancellationToken);

        // Materialization happens inside AgentRunWorker right after the run
        // completes; give that same cycle a bounded window to land.
        var plan = await WaitForPlanAsync(live, projectId, TimeSpan.FromMinutes(2), cancellationToken);

        stopwatch.Stop();

        var structured = await ReadStructuredStatusAsync(live, agentRunId, cancellationToken);

        var keys = plan?.Tasks.Select(t => t.PlanKey).ToList() ?? new List<string>();
        var graph = plan is null
            ? "-"
            : string.Join(" ", plan.Tasks.Select(t =>
                t.DependsOn.Count == 0 ? t.PlanKey : $"{t.PlanKey}<-[{string.Join(",", t.DependsOn)}]"));

        string? failureStage = null;

        if (plan is null)
        {
            // Re-materializing a terminal run is side-effect free and returns
            // the same classified outcome the worker already logged — the §6
            // stage, without scraping the log.
            await using var scope = live.Host.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IProjectPlanner>()
                .MaterializeAsync(agentRunId, cancellationToken);
            failureStage = $"{result.Outcome}: {result.Message}";
            _output.WriteLine($"[run {run}] FAILED — {failureStage}");
        }

        var criteriaValid = plan is not null
            && await AllWorkItemsHaveReadableCriteriaAsync(live, projectId, cancellationToken);

        return new PlannerAttempt(
            run, projectId, agentRunId, terminalRun.Status, structured,
            PlanValid: plan is not null,
            CriteriaValid: criteriaValid,
            Persisted: plan is not null,
            PlanKeys: keys,
            DependencyGraph: graph,
            FailureStage: failureStage,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Phase 18.25 §19 — reconstructs the lifecycle from the persisted audit
    /// trail and asserts that the start and every intermediate transition were
    /// written by the lifecycle owner ("System" /
    /// <c>ProjectLifecycleOrchestrator</c>), in order, with timestamps. Test
    /// code issues none of these, so a matching trail can only have come from
    /// the Factory itself.
    /// </summary>
    private async Task AssertLifecycleEvidenceAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var entries = await db.AuditLogEntries
            .Where(e => e.ProjectId == projectId)
            .OrderBy(e => e.TimestampUtc)
            .ToListAsync(cancellationToken);

        _output.WriteLine("");
        _output.WriteLine("| TimestampUtc | Event | Outcome | Actor |");
        _output.WriteLine("|---|---|---|---|");

        foreach (var e in entries)
        {
            _output.WriteLine(
                $"| {e.TimestampUtc:HH:mm:ss.fff} | {e.EventType} | {e.Outcome} | {e.ActorType}/{e.ActorId ?? "-"} |");
        }

        _output.WriteLine("");

        var start = entries.SingleOrDefault(e => e.EventType == AuditEventType.WorkflowStarted);
        Assert.True(start is not null, "No lifecycle-start audit entry was recorded.");

        var automatic = entries
            .Where(e => e.EventType == AuditEventType.WorkflowStateChanged
                && e.ActorId == nameof(ProjectLifecycleOrchestrator))
            .ToList();

        foreach (var expected in new[]
                 {
                     ProjectStatus.Specification, ProjectStatus.Architecture,
                     ProjectStatus.Planning, ProjectStatus.Implementation
                 })
        {
            var entry = automatic.SingleOrDefault(e => e.Outcome == expected.ToString());

            Assert.True(
                entry is not null,
                $"No automatic lifecycle transition to {expected} was recorded by the lifecycle owner.");

            Assert.Equal("System", entry!.ActorType);
            Assert.True(entry.TimestampUtc >= start!.TimestampUtc, $"{expected} predates the lifecycle start.");
        }

        // The Planner dispatch is the lifecycle owner's too — not the test's.
        Assert.Contains(
            entries,
            e => e.EventType == AuditEventType.AgentStarted && e.ActorId == nameof(ProjectLifecycleOrchestrator));
    }

    /// <summary>
    /// Phase 18.25 — waits for the Planner AgentRun the lifecycle orchestrator
    /// dispatched, read back through the Planner's own production query. The
    /// test never creates one.
    /// </summary>
    private async Task<AgentRun> WaitForPlannerRunAsync(
        LiveWorkerHost live, Guid projectId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var run = await scope.ServiceProvider.GetRequiredService<IProjectPlanner>()
                    .GetLatestRunAsync(projectId, cancellationToken);

                if (run is not null)
                {
                    return run;
                }
            }

            await Task.Delay(2000, cancellationToken);
        }

        throw new TimeoutException($"The lifecycle owner never dispatched a Planner AgentRun for project {projectId}.");
    }

    /// <summary>
    /// The operator-level Project transitions that precede planning. These are
    /// the Project's own validated status machine (the same calls the HTTP API
    /// exposes) — not a WorkItem lifecycle shortcut, and nothing here forces an
    /// illegal state.
    /// </summary>
    private static async Task MoveProjectToPlanningAsync(
        ProjectService projectService, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projectService.GetAsync(projectId, cancellationToken);

        if (project is not null && project.Status == ProjectStatus.Created)
        {
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Analyzing, cancellationToken);
        }

        await projectService.TransitionStatusAsync(projectId, ProjectStatus.Specification, cancellationToken);
        await projectService.TransitionStatusAsync(projectId, ProjectStatus.Architecture, cancellationToken);
        await projectService.TransitionStatusAsync(projectId, ProjectStatus.Planning, cancellationToken);
    }

    private async Task<AgentRun> WaitForAgentRunTerminalAsync(
        LiveWorkerHost live, Guid agentRunId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        AgentRunStatus? last = null;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var run = await scope.ServiceProvider.GetRequiredService<IAgentRunRepository>()
                    .GetByIdAsync(agentRunId, cancellationToken);

                if (run is not null)
                {
                    if (run.Status != last)
                    {
                        last = run.Status;
                        _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Planner AgentRun {agentRunId} -> {run.Status}");
                    }

                    if (AgentRunTransitions.IsTerminal(run.Status))
                    {
                        return run;
                    }
                }
            }

            await Task.Delay(2000, cancellationToken);
        }

        throw new TimeoutException($"Planner AgentRun {agentRunId} never reached a terminal status. Last: {last}.");
    }

    private static async Task<ProjectPlanResult?> WaitForPlanAsync(
        LiveWorkerHost live, Guid projectId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var plan = await scope.ServiceProvider.GetRequiredService<IProjectPlanService>()
                    .GetPlanAsync(projectId, cancellationToken);

                if (plan is not null)
                {
                    return plan;
                }
            }

            await Task.Delay(2000, cancellationToken);
        }

        return null;
    }

    private static async Task<string> ReadStructuredStatusAsync(
        LiveWorkerHost live, Guid agentRunId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IAgentRunRepository>()
            .GetByIdAsync(agentRunId, cancellationToken);

        if (string.IsNullOrWhiteSpace(run?.StructuredResult))
        {
            return "none";
        }

        var parsed = System.Text.Json.JsonSerializer.Deserialize<StructuredAgentResult>(
            run.StructuredResult, StructuredAgentResult.JsonOptions);

        return parsed?.Status.ToString() ?? "unreadable";
    }

    private static async Task<bool> AllWorkItemsHaveReadableCriteriaAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>()
            .GetByProjectIdAsync(projectId, cancellationToken);

        return items.Count > 0 && items.All(i =>
            AcceptanceCriteriaContract.Parse(i.AcceptanceCriteria).Status == AcceptanceCriteriaParseStatus.Valid);
    }

    private async Task AssertEveryWorkItemHasReadableCriteriaAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>()
            .GetByProjectIdAsync(projectId, cancellationToken);

        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            var parsed = AcceptanceCriteriaContract.Parse(item.AcceptanceCriteria);

            _output.WriteLine($"  {item.Title} [{item.Type}] criteria={parsed.Status} ({parsed.Criteria.Count})");

            Assert.Equal(AcceptanceCriteriaParseStatus.Valid, parsed.Status);
        }
    }

    private async Task AssertPersistedGraphMatchesPlanAsync(
        LiveWorkerHost live,
        Guid projectId,
        IReadOnlyList<ProjectPlanTaskResult> planTasks,
        CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var items = await db.WorkItems
            .Include(w => w.Dependencies)
            .Where(w => w.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var idToKey = planTasks.ToDictionary(t => t.WorkItemId, t => t.PlanKey);

        Assert.Equal(planTasks.Count, items.Count);

        foreach (var task in planTasks)
        {
            var item = items.Single(w => w.Id == task.WorkItemId);

            var persisted = item.Dependencies
                .Select(d => idToKey[d.DependsOnWorkItemId])
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            _output.WriteLine($"  {task.PlanKey} dependsOn [{string.Join(",", persisted)}]");

            Assert.Equal(task.DependsOn.OrderBy(k => k, StringComparer.Ordinal).ToList(), persisted);
        }
    }

    private async Task ReportAgentRunsAsync(LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var definitions = (await new AgentDefinitionRepository(db).GetAllAsync(cancellationToken))
            .ToDictionary(d => d.Id, d => d.AgentType);

        var runs = await db.AgentRuns
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var run in runs)
        {
            _output.WriteLine(
                $"  AgentRun {run.Id} type={definitions.GetValueOrDefault(run.AgentDefinitionId, "?")} " +
                $"status={run.Status} workItem={run.WorkItemId}");
        }
    }

    private void WriteReliabilityTable(IReadOnlyList<PlannerAttempt> attempts)
    {
        _output.WriteLine("");
        _output.WriteLine("| Run | Hermes | Structured Result | Plan Valid | Criteria Valid | Persisted | Duration | Plan |");
        _output.WriteLine("|---|---|---|---|---|---|---|---|");

        foreach (var a in attempts)
        {
            _output.WriteLine(
                $"| {a.Run} | {a.HermesStatus} | {a.StructuredResult} | {a.PlanValid} | {a.CriteriaValid} | " +
                $"{a.Persisted} | {a.Duration.TotalSeconds:F0}s | {a.DependencyGraph} |");
        }

        foreach (var a in attempts.Where(a => a.FailureStage is not null))
        {
            _output.WriteLine($"Run {a.Run} failure: {a.FailureStage}");
        }

        _output.WriteLine("");
    }

    /// <summary>What one Planner-generated WorkItem actually did, as observed.</summary>
    private sealed record WorkItemObservation(
        Guid WorkItemId,
        string PlanKey,
        IReadOnlyList<string> StatusTimeline,
        int RepairAttemptCount);

    /// <summary>
    /// Phase 18.24 §8/§11 — drives real scheduler cycles until every
    /// Planner-generated WorkItem is Completed, recording each one's status
    /// timeline and asserting the dependency invariant on every snapshot: a
    /// WorkItem may not leave the undispatched states while any prerequisite is
    /// still unresolved.
    ///
    /// Waiting on all of them together, rather than one at a time in plan-key
    /// order, is what makes that invariant observable at all — and it keeps a
    /// failure message pointing at the WorkItem that actually stalled, which the
    /// previous revision's ordering did not (Phase 18.23 §11f).
    /// </summary>
    private async Task<IReadOnlyList<WorkItemObservation>> DriveGeneratedWorkItemsAsync(
        LiveWorkerHost live,
        Guid projectId,
        IReadOnlyList<ProjectPlanTaskResult> planTasks,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var idByKey = planTasks.ToDictionary(t => t.PlanKey, t => t.WorkItemId, StringComparer.Ordinal);
        var keyById = planTasks.ToDictionary(t => t.WorkItemId, t => t.PlanKey);

        var prerequisites = planTasks.ToDictionary(
            t => t.WorkItemId,
            t => t.DependsOn.Where(idByKey.ContainsKey).Select(k => idByKey[k]).ToList());

        var timelines = planTasks.ToDictionary(t => t.WorkItemId, _ => new List<string>());
        var repairCounts = planTasks.ToDictionary(t => t.WorkItemId, _ => 0);

        // Draft/Ready/Blocked are the states a WorkItem sits in before the
        // scheduler has ever handed it to an agent. Anything else means it was
        // dispatched at least once.
        static bool Undispatched(string status) =>
            status is nameof(WorkItemStatus.Draft)
                or nameof(WorkItemStatus.Ready)
                or nameof(WorkItemStatus.Blocked);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // Phase 18.25 — the real TaskSchedulerHostedService drives the
            // cycles now; this loop only observes what it produced.
            Dictionary<Guid, WorkItem> snapshot;

            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var items = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>()
                    .GetByProjectIdAsync(projectId, cancellationToken);

                snapshot = items.Where(i => timelines.ContainsKey(i.Id)).ToDictionary(i => i.Id);
            }

            foreach (var (workItemId, item) in snapshot)
            {
                var timeline = timelines[workItemId];

                if (timeline.Count == 0 || timeline[^1] != item.Status)
                {
                    timeline.Add(item.Status);
                    _output.WriteLine(
                        $"[{DateTime.UtcNow:HH:mm:ss}] {keyById[workItemId]} -> {item.Status}" +
                        (item.RepairAttemptCount > 0 ? $" (RepairAttemptCount={item.RepairAttemptCount})" : string.Empty));
                }

                repairCounts[workItemId] = Math.Max(repairCounts[workItemId], item.RepairAttemptCount);

                // §11 — never "prerequisite unresolved, dependent Running".
                if (!Undispatched(item.Status))
                {
                    foreach (var prerequisiteId in prerequisites[workItemId])
                    {
                        var prerequisite = snapshot.GetValueOrDefault(prerequisiteId);

                        Assert.True(
                            prerequisite?.Status == nameof(WorkItemStatus.Completed),
                            $"Dependency violation: {keyById[workItemId]} is {item.Status} while its prerequisite " +
                            $"{keyById[prerequisiteId]} is {prerequisite?.Status ?? "(missing)"}.");
                    }
                }

                if (Enum.TryParse<WorkItemStatus>(item.Status, out var parsed)
                    && WorkItemTransitions.IsTerminal(parsed)
                    && parsed != WorkItemStatus.Completed)
                {
                    throw new InvalidOperationException(
                        $"{keyById[workItemId]} reached terminal status {item.Status} instead of Completed. " +
                        $"Error: {item.CurrentError}");
                }
            }

            if (snapshot.Count == timelines.Count
                && snapshot.Values.All(i => i.Status == nameof(WorkItemStatus.Completed)))
            {
                return timelines
                    .Select(kv => new WorkItemObservation(kv.Key, keyById[kv.Key], kv.Value, repairCounts[kv.Key]))
                    .ToList();
            }

            await Task.Delay(2000, cancellationToken);
        }

        var outstanding = string.Join(
            ", ",
            timelines
                .Where(kv => kv.Value.Count == 0 || kv.Value[^1] != nameof(WorkItemStatus.Completed))
                .Select(kv => $"{keyById[kv.Key]}={(kv.Value.Count == 0 ? "(unseen)" : kv.Value[^1])}"));

        throw new TimeoutException($"Timed out driving the Planner-generated WorkItems. Outstanding: {outstanding}.");
    }

    /// <summary>
    /// Phase 18.24 §11 — the same dependency claim, restated from persisted
    /// scheduler/AgentRun evidence rather than from the polling loop alone: no
    /// AgentRun for a dependent WorkItem may have started before its prerequisite
    /// WorkItem reached Completed.
    /// </summary>
    private async Task AssertDependentRunsStartedAfterPrerequisitesAsync(
        LiveWorkerHost live,
        Guid projectId,
        IReadOnlyList<ProjectPlanTaskResult> planTasks,
        CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var items = (await db.WorkItems.Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken))
            .ToDictionary(w => w.Id);

        var runs = await db.AgentRuns
            .Where(r => r.ProjectId == projectId && r.WorkItemId != null)
            .ToListAsync(cancellationToken);

        var idByKey = planTasks.ToDictionary(t => t.PlanKey, t => t.WorkItemId, StringComparer.Ordinal);

        foreach (var task in planTasks.Where(t => t.DependsOn.Count > 0))
        {
            var starts = runs
                .Where(r => r.WorkItemId == task.WorkItemId)
                .Select(r => r.StartedAtUtc ?? r.CreatedAtUtc)
                .ToList();

            Assert.NotEmpty(starts);

            var firstStart = starts.Min();

            foreach (var dependsOnKey in task.DependsOn.Where(idByKey.ContainsKey))
            {
                var prerequisite = items[idByKey[dependsOnKey]];

                _output.WriteLine(
                    $"DEPENDENCY {task.PlanKey} firstAgentRunStart={firstStart:HH:mm:ss.fff} " +
                    $"after {dependsOnKey} CompletedAtUtc={prerequisite.CompletedAtUtc:HH:mm:ss.fff}");

                Assert.NotNull(prerequisite.CompletedAtUtc);
                Assert.True(
                    firstStart >= prerequisite.CompletedAtUtc,
                    $"{task.PlanKey} started at {firstStart:O} before its prerequisite {dependsOnKey} " +
                    $"completed at {prerequisite.CompletedAtUtc:O}.");
            }
        }
    }

    /// <summary>
    /// §16/§17 — records which provider a run used, without ever recording the
    /// credential.
    /// </summary>
    private static string DescribeBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath}"
            : "(unparsable)";

    private async Task<Project> WaitForProjectStatusAsync(
        LiveWorkerHost live, Guid projectId, TimeSpan timeout, ProjectStatus expected)
    {
        var deadline = DateTime.UtcNow + timeout;
        ProjectStatus? lastStatus = null;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var project = await scope.ServiceProvider.GetRequiredService<IProjectRepository>().GetByIdAsync(projectId);

                if (project is not null)
                {
                    if (project.Status != lastStatus)
                    {
                        lastStatus = project.Status;
                        _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Project {projectId} -> {project.Status}");
                    }

                    if (project.Status == expected)
                    {
                        return project;
                    }
                }
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"Timed out waiting for Project {projectId} to reach {expected}. Last: {lastStatus}.");
    }
}
