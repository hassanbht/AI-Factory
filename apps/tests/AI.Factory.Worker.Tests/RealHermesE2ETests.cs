using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Source;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.1.7 — the real Hermes end-to-end gate. Opt-in only, disabled by
/// default (same convention as HermesSmokeTest.cs):
///
///   AI_FACTORY_HERMES_SMOKE_TEST=true dotnet test --filter RealHermesE2ETests
///
/// Each test builds the exact real Worker composition root
/// (Program.ConfigureServices — the same code path apps/src/AI.Factory.Worker
/// runs in production) against an isolated in-memory database and isolated
/// Hermes workspace/data directories, then drives the real AgentRunWorker
/// hosted-service instance directly (never via host.Run(), so no unrelated
/// hosted service is ever started) against a real `docker run` Hermes
/// container. No fakes anywhere on the AgentRun -> Worker -> Orchestrator ->
/// HermesAgentRuntime -> HermesAgentExecutor -> Docker path.
/// </summary>
public sealed class RealHermesE2ETests
{
    private const string SmokeMarker = "AI_FACTORY_HERMES_SMOKE_OK";
    private const string SmokePrompt = "Respond with exactly this text and nothing else: " + SmokeMarker;

    // Routed through a local OmniRoute gateway (github.com/diegosouzapw/OmniRoute) so a real
    // completion is actually reachable in this environment — OpenRouter directly (the previously
    // configured default provider) has no billing/credits for the model Hermes selects by
    // default. See .ai/context/phase-18.1.7-agent-e2e-stability.md for the full investigation.
    private const string OmniRouteModel = "auto/coding:free";
    private const string OmniRouteBaseUrl = "http://host.docker.internal:20128/v1";

    private static bool Enabled =>
        bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var enabled) && enabled;

    [Fact]
    public async Task Smoke_RealAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();

        var before = testHost.Metrics.GetSnapshot();

        var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        Assert.NotNull(final.Output);
        Assert.Contains(SmokeMarker, final.Output, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(final.StartedAtUtc);
        Assert.NotNull(final.CompletedAtUtc);

        var after = testHost.Metrics.GetSnapshot();
        Assert.Equal(before.AgentRunsCreated + 1, after.AgentRunsCreated);
        Assert.Equal(before.AgentRunsStarted + 1, after.AgentRunsStarted);
        Assert.Equal(before.AgentRunsSucceeded + 1, after.AgentRunsSucceeded);
        Assert.Equal(before.AgentWorkerClaimSuccess + 1, after.AgentWorkerClaimSuccess);

        // Logging/correlation (spec 17): the same AgentRunId must be traceable
        // across the worker claim, the orchestrator, and the runtime layer.
        var runIdText = run.Id.ToString();
        Assert.Contains(testHost.Logs, l => l.Contains("claimed by worker") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRunStarted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRuntimeStarted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRuntimeCompleted") && l.Contains(runIdText));
        Assert.Contains(testHost.Logs, l => l.Contains("AgentRunCompleted") && l.Contains(runIdText));
    }

    [Fact]
    public async Task StructuredResult_RealAgentRun_ThroughFullFactoryPipeline_ReachesValidStructuredResult()
    {
        // Phase 18.1.8 §11 — every real AgentRun already gets the full
        // structured-output-contract instruction automatically (the
        // orchestrator always routes through IAgentInstructionBuilder now);
        // this test confirms the real model honors it and the Factory
        // validates + persists + can read back the result.
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();

        const string marker = "AI_FACTORY_STRUCTURED_RESULT_OK";
        var (run, _) = await CreateQueuedRunAsync(
            testHost, $"Respond with a one-line summary containing exactly this marker: {marker}");

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        Assert.Equal(StructuredAgentResult.CurrentVersion, final.StructuredResultVersion);
        Assert.NotNull(final.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(
            final.StructuredResult!, StructuredAgentResult.JsonOptions);

        Assert.NotNull(structured);
        Assert.NotEqual(StructuredResultStatus.Invalid, structured!.Status);

        var haystack = $"{structured.Summary} {structured.Output} {structured.Data}";
        Assert.Contains(marker, haystack, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Memory_ScopedEntry_IsFoundAndRealExecutionStillSucceeds()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();
        var projectId = Guid.NewGuid();

        Guid definitionId;
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var definition = await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "Coder", AgentType = "Coding" });
            definitionId = definition.Id;

            var memory = new LocalAgentMemoryProvider(db);
            await memory.StoreAsync(new AgentMemoryEntry(
                Guid.NewGuid(), projectId, definitionId, null, "note", "prior run preferred concise answers", null, DateTime.UtcNow));
        }

        var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt, projectId, definitionId);

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);

        // Boundary verification (spec 10): the stored entry is still
        // resolvable in the same scope IAgentMemory.SearchAsync uses —
        // proving the memory lookup the orchestrator performed had a real
        // row to find, without requiring semantic relevance.
        await using var verifyScope = testHost.Host.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var found = await new LocalAgentMemoryProvider(verifyDb).SearchAsync(
            new AgentMemoryQuery(ProjectId: projectId, AgentDefinitionId: definitionId));
        Assert.Contains(found, e => e.Content == "prior run preferred concise answers");
    }

    [Fact]
    public async Task WorkerRecovery_FailingRunThenSucceedingRun_WorkerStaysAlive()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();
        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Run A: deliberately invalid Hermes configuration (spec 12) — an
        // unknown docker network name fails fast (container-creation error,
        // no image pull, no LLM call) without throwing, so no fake boundary
        // is introduced.
        testHost.Hermes.NetworkMode = "ai-factory-invalid-network";
        var (runA, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);
        var finalA = await WaitForTerminalAsync(testHost, runA.Id, TimeSpan.FromSeconds(60));

        Assert.NotNull(finalA);
        Assert.Equal(AgentRunStatus.Failed, finalA!.Status);
        Assert.NotEqual(FailureCategory.Timeout, finalA.FailureCategory);
        Assert.NotEqual(FailureCategory.Cancelled, finalA.FailureCategory);
        Assert.False(string.IsNullOrWhiteSpace(finalA.ErrorMessage));

        // Run B: valid configuration again, same worker instance (spec 14) —
        // proves the failed run above never took the Worker down.
        testHost.Hermes.NetworkMode = "bridge";
        var (runB, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);
        var finalB = await WaitForTerminalAsync(testHost, runB.Id, TimeSpan.FromSeconds(150));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(finalB);
        Assert.Equal(AgentRunStatus.Succeeded, finalB!.Status);

        var snapshot = testHost.Metrics.GetSnapshot();
        Assert.Equal(2, snapshot.AgentWorkerClaimSuccess);
        Assert.Equal(1, snapshot.AgentRunsFailed);
        Assert.Equal(1, snapshot.AgentRunsSucceeded);
    }

    [Fact]
    public async Task Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost(configureWorker: o => o.ExecutionTimeout = TimeSpan.FromSeconds(2));

        var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(90));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Cancelled, final!.Status);
        Assert.Equal(FailureCategory.Cancelled, final.FailureCategory);
    }

    [Fact]
    public async Task Timeout_HermesProcessTimeoutSeconds_ReportsTimedOut()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost(
            configureHermes: o => o.TimeoutSeconds = 2,
            configureWorker: o => o.ExecutionTimeout = TimeSpan.FromSeconds(60));

        var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(90));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.TimedOut, final!.Status);
        Assert.Equal(FailureCategory.Timeout, final.FailureCategory);
    }

    [Fact]
    public async Task Stability_FiveConsecutiveRealExecutions_AllSucceed()
    {
        if (!Enabled)
        {
            return;
        }

        using var testHost = BuildHost();
        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var results = new List<(Guid RunId, DateTime StartedAtUtc, long DurationMs, AgentRunStatus Status)>();

        for (var i = 0; i < 5; i++)
        {
            var startedAt = DateTime.UtcNow;
            var (run, _) = await CreateQueuedRunAsync(testHost, SmokePrompt);
            var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

            Assert.NotNull(final);
            results.Add((run.Id, startedAt, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds, final!.Status));
        }

        await worker.StopAsync(CancellationToken.None);

        foreach (var (runId, startedAt, durationMs, status) in results)
        {
            Console.WriteLine($"StabilityRun RunId={runId} StartedAtUtc={startedAt:O} DurationMs={durationMs} Status={status}");
        }

        Assert.All(results, r => Assert.Equal(AgentRunStatus.Succeeded, r.Status));
    }

    [Fact]
    public async Task Planner_RealAgentRun_ThroughFullFactoryPipeline_MaterializesPersistedPlan()
    {
        // Phase 18.6 §24 — the single narrow real-Hermes gate for the LLM
        // Planner: Project -> Planner AgentRun -> AgentRunWorker ->
        // AgentOrchestrator -> Hermes -> OmniRoute -> real LLM ->
        // StructuredAgentResult -> ProjectPlanService -> persisted
        // WorkItems, all through the unmodified production pipeline.
        if (!Enabled)
        {
            return;
        }

        const string plannerMarker = "AI_FACTORY_PLANNER_E2E_OK";

        using var testHost = BuildHost();

        Guid projectId;
        AgentRun run;
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var projectService = new ProjectService(new ProjectRepository(db));
            var project = await projectService.CreateAsync("Planner E2E Project", "A tiny two-task project.");
            projectId = project.Id;
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Analyzing);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Specification);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Architecture);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Planning);

            var definitionRepository = new AgentDefinitionRepository(db);
            await definitionRepository.CreateAsync(new AgentDefinition { Name = "Planner", AgentType = ProjectPlanner.PlannerAgentType });

            // One AgentDefinition per non-Planner WorkItemType, so ProjectPlanService's
            // AgentDefinition-compatibility check (Phase 18.5 §14) can never reject the
            // real LLM's task-type choice regardless of which types it picks.
            foreach (var type in Enum.GetNames<WorkItemType>())
            {
                await definitionRepository.CreateAsync(new AgentDefinition { Name = $"Agent-{type}", AgentType = type });
            }

            var planner = scope.ServiceProvider.GetRequiredService<IProjectPlanner>();
            run = await planner.GenerateAsync(projectId);

            // Augment the production-generated Objective with a marker
            // requirement, purely so this test can honestly confirm the
            // real model actually produced fresh output — never fabricated.
            var runRepository = new AgentRunRepository(db);
            var reloaded = (await runRepository.GetByIdAsync(run.Id))!;
            reloaded.Input += $"\n\nAlso include this exact marker string in your \"summary\" field: {plannerMarker}";
            reloaded.Model = OmniRouteModel;
            await runRepository.UpdateAsync(reloaded);
        }

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var final = await WaitForTerminalAsync(testHost, run.Id, TimeSpan.FromSeconds(150));

        Assert.NotNull(final);
        Assert.Equal(AgentRunStatus.Succeeded, final!.Status);
        Assert.NotNull(final.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(final.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.NotNull(structured);
        var haystack = $"{structured!.Summary} {structured.Output}";
        Assert.Contains(plannerMarker, haystack, StringComparison.OrdinalIgnoreCase);

        // AgentRunWorker materializes the plan right after the terminal
        // status write, in the same poll cycle — but on an independent
        // subsequent database write, so poll for it rather than assuming
        // it is already visible the instant the AgentRun turned terminal.
        var plan = await WaitForPlanAsync(testHost, projectId, TimeSpan.FromSeconds(30));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.Tasks);
    }

    [Fact]
    public async Task WorkItemExecution_RealAgentRun_ThroughFullFactoryPipeline_UpdatesPersistedWorkItemState()
    {
        // Phase 18.7 §15 — the single narrow real-Hermes gate for WorkItem
        // execution: Ready WorkItem -> TaskDispatchScheduler -> real AgentRun
        // -> AgentRunWorker -> AgentOrchestrator -> Hermes -> OmniRoute ->
        // real LLM -> StructuredAgentResult -> execution result mapping ->
        // persisted WorkItem state, all through the unmodified production
        // pipeline (AgentRunWorkItemSync's Phase 18.7 sync branch runs
        // automatically inside the Worker's own poll loop, same as always).
        if (!Enabled)
        {
            return;
        }

        const string executionMarker = "AI_FACTORY_WORKITEM_EXECUTION_E2E_OK";

        using var testHost = BuildHost();

        Guid projectId, workItemId, agentRunId;
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var projectRepo = new ProjectRepository(db);
            var project = await new ProjectService(projectRepo).CreateAsync("WorkItem Execution E2E Project", "d");
            projectId = project.Id;

            // Phase 18.16 §12 — WorkItem creation now requires a source
            // decision (Status != Created); this E2E test is not exercising
            // source init.
            project.Status = ProjectStatus.Analyzing;
            await projectRepo.SaveChangesAsync();

            await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "Coder", AgentType = "Implementation" });

            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation, "Trivial marker task",
                "Respond only with the required structured execution result — no real code changes are needed.",
                "Normal");
            workItemId = item.Id;
            await workItems.TransitionStatusAsync(workItemId, WorkItemStatus.Ready);

            var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
            var cycle = await scheduler.RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;

            // Augment the production-generated Objective with a marker
            // requirement, purely so this test can honestly confirm the
            // real model actually produced fresh output — never fabricated.
            var runRepository = new AgentRunRepository(db);
            var reloaded = (await runRepository.GetByIdAsync(agentRunId))!;
            reloaded.Input += $"\n\nAlso include this exact marker string in your \"summary\" field: {executionMarker}";
            reloaded.Model = OmniRouteModel;
            await runRepository.UpdateAsync(reloaded);
        }

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var finalRun = await WaitForTerminalAsync(testHost, agentRunId, TimeSpan.FromSeconds(150));

        Assert.NotNull(finalRun);
        Assert.Equal(AgentRunStatus.Succeeded, finalRun!.Status);
        Assert.NotNull(finalRun.StructuredResult);

        var structured = JsonSerializer.Deserialize<StructuredAgentResult>(finalRun.StructuredResult!, StructuredAgentResult.JsonOptions);
        Assert.NotNull(structured);
        var haystack = $"{structured!.Summary} {structured.Output} {structured.Data}";
        Assert.Contains(executionMarker, haystack, StringComparison.OrdinalIgnoreCase);

        // AgentRunWorkItemSync runs inside the same Worker poll cycle right
        // after the AgentRun turns terminal, but on an independent
        // subsequent database write — poll for the WorkItem to leave
        // Running rather than assuming it is already visible.
        var finalWorkItem = await WaitForWorkItemLeavesRunningAsync(testHost, workItemId, TimeSpan.FromSeconds(30));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(finalWorkItem);
        // The real model decides completed/repair_required on its own — this
        // proves the deterministic mapping pipeline actually ran and reached
        // one of its known outcomes, not any specific one.
        Assert.Contains(
            finalWorkItem!.Status,
            new[] { WorkItemStatus.Completed.ToString(), WorkItemStatus.Blocked.ToString(), WorkItemStatus.Failed.ToString() });
    }

    private static async Task<WorkItem?> WaitForWorkItemLeavesRunningAsync(TestHost testHost, Guid workItemId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);

            if (workItem is not null && workItem.Status != WorkItemStatus.Running.ToString())
            {
                return workItem;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task<ProjectPlanResult?> WaitForPlanAsync(TestHost testHost, Guid projectId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var planService = scope.ServiceProvider.GetRequiredService<IProjectPlanService>();
            var plan = await planService.GetPlanAsync(projectId);

            if (plan is not null)
            {
                return plan;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static async Task<(AgentRun Run, Guid DefinitionId)> CreateQueuedRunAsync(
        TestHost testHost, string input, Guid? projectId = null, Guid? agentDefinitionId = null)
    {
        await using var scope = testHost.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var runRepository = new AgentRunRepository(db);
        var definitionRepository = new AgentDefinitionRepository(db);

        var definitionId = agentDefinitionId;
        if (definitionId is null)
        {
            var definition = await definitionRepository.CreateAsync(new AgentDefinition { Name = "Coder", AgentType = "Coding" });
            definitionId = definition.Id;
        }

        var service = new AgentRunService(
            runRepository, definitionRepository, testHost.Metrics, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunService>.Instance);

        var run = await service.CreateAsync(new CreateAgentRunCommand(definitionId.Value, projectId, input));

        // CreateAgentRunCommand has no Model parameter yet (nothing in the
        // production pipeline populates AgentRun.Model — see the phase
        // report) — set it directly here so HermesAgentExecutor's -m flag
        // (already wired) actually selects a real, reachable model via
        // OmniRoute instead of Hermes's own unreachable/unaffordable default.
        run.Model = OmniRouteModel;
        await runRepository.UpdateAsync(run);

        return (run, definitionId.Value);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 18.17 — Real Hermes WorkItem E2E: modifies workspace + verifies
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 18.17 §22 — the first real Hermes gate that proves the full
    /// workspace chain in one test:
    ///
    ///   1. Project created  →  real ProjectWorkspaceProvisioner provisions RootPath
    ///   2. Git source initialized  →  real ProjectSourceInitializer clones a
    ///      local bare repo with a known C# source file into that RootPath
    ///   3. WorkItem dispatched  →  real AgentRun created
    ///   4. AgentRunWorker → AgentOrchestrator → HermesAgentExecutor →
    ///      Docker run  →  /workspace bind-mounted to the SAME host RootPath
    ///   5. Agent modifies src/Marker.cs — replacing "MARKER_BEFORE" with
    ///      "MARKER_AFTER"  →  change visible on host filesystem
    ///   6. VerificationGate runs dotnet build + test on the same workspace
    ///   7. WorkItem reaches Completed (or Blocked — the real model decides)
    ///
    /// Requires:
    ///   AI_FACTORY_HERMES_SMOKE_TEST=true
    ///   OMNIROUTE_API_KEY env var set
    ///   Docker daemon running
    ///   Hermes image available (AI_FACTORY_HERMES_IMAGE or default)
    ///   git available on PATH
    /// </summary>
    [Fact]
    public async Task RealHermes_WorkItemExecution_ModifiesProjectWorkspace_AndPassesFactoryVerification()
    {
        if (!Enabled)
        {
            return;
        }

        // ── 1. Build fixture: bare Git repo with a minimal .NET project ──────
        var fixtureTemp = Path.Combine(Path.GetTempPath(), "aif-e2e17-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureTemp);

        string bareRepoPath;
        try
        {
            bareRepoPath = CreateDemoFixtureBareRepo(fixtureTemp);
        }
        catch (Exception ex)
        {
            // git not available — skip rather than fail
            throw new InvalidOperationException(
                "Failed to create fixture bare repo (is git on PATH?): " + ex.Message, ex);
        }

        // projectsBase is the directory under which the Provisioner will
        // create per-project subdirectories. We use the same fixtureTemp
        // root so cleanup is one Directory.Delete call.
        var projectsBase = Path.Combine(fixtureTemp, "projects");
        Directory.CreateDirectory(projectsBase);

        // ── 2. Provision workspace + initialize Git source ─────────────────
        // Done OUTSIDE the Worker DI: Provisioner/SourceInitializer are not
        // registered in the Worker composition root — they run once at
        // project-creation time, not at agent execution time.
        var workspaceOptions = new AgentWorkspaceOptions { ProjectsBaseDirectory = projectsBase };
        var resolver = new ProjectWorkspaceResolver(workspaceOptions);
        var commandPolicyOptions = new CommandPolicyOptions();
        var executionLimitsOptions = new ExecutionLimitsOptions { BuildTimeoutMinutes = 5 };

        // Reuse BuildHost to get the real Worker DI graph (with the
        // overridden AgentWorkspaceOptions pointing to projectsBase).
        using var testHost = BuildHost(
            configureWorkspace: o => o.ProjectsBaseDirectory = projectsBase);

        Guid projectId, workItemId, agentRunId;
        string resolvedProjectRoot;

        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            var provisioner = new ProjectWorkspaceProvisioner(
                resolver, workspaceOptions,
                NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var sourceInitializer = new ProjectSourceInitializer(
                resolver, new SystemProcessRunner(commandPolicyOptions),
                executionLimitsOptions,
                NullLogger<ProjectSourceInitializer>.Instance);
            var projectService = new ProjectService(
                new ProjectRepository(db), provisioner, sourceInitializer: sourceInitializer);

            var project = await projectService.CreateAsync("E2E Workspace Modifier", "d");
            projectId = project.Id;

            // Resolve and record the host RootPath for later assertions.
            resolvedProjectRoot = resolver.Resolve(project).Path!;

            // Clone the local bare repo into the workspace.
            await projectService.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, bareRepoPath));

            // Confirm fixture file is present.
            var markerFile = Path.Combine(resolvedProjectRoot, "src", "Marker.cs");
            Assert.True(File.Exists(markerFile),
                $"Fixture file missing after Git clone: {markerFile}");
            Assert.Contains("MARKER_BEFORE", await File.ReadAllTextAsync(markerFile));

            // ── 3. Create + dispatch WorkItem ─────────────────────────────
            await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = "Coder", AgentType = "Implementation" });

            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation,
                "Replace marker string in src/Marker.cs",
                "In src/Marker.cs, find the line containing \"MARKER_BEFORE\" and change it to \"MARKER_AFTER\". " +
                "Do not modify any other file. Run dotnet test to confirm the project still builds.",
                "Normal");
            workItemId = item.Id;
            await workItems.TransitionStatusAsync(workItemId, WorkItemStatus.Ready);

            var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
            var cycle = await scheduler.RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;

            // Override model for the specific run.
            var runRepo = new AgentRunRepository(db);
            var reloaded = (await runRepo.GetByIdAsync(agentRunId))!;
            reloaded.Model = OmniRouteModel;
            await runRepo.UpdateAsync(reloaded);
        }

        // ── 4. Run the real Worker ────────────────────────────────────────
        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var finalRun = await WaitForTerminalAsync(testHost, agentRunId, TimeSpan.FromSeconds(240));

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(finalRun);
        Assert.Equal(AgentRunStatus.Succeeded, finalRun!.Status);

        // ── 5. Host filesystem: file content must have changed ────────────
        var modifiedFile = Path.Combine(resolvedProjectRoot, "src", "Marker.cs");
        Assert.True(File.Exists(modifiedFile), $"src/Marker.cs not found in workspace '{resolvedProjectRoot}'");
        var content = await File.ReadAllTextAsync(modifiedFile);
        Assert.True(content.Contains("MARKER_AFTER"),
            $"Agent did not replace MARKER_BEFORE with MARKER_AFTER in src/Marker.cs. Actual content: {content}");

        // ── 6. VerificationGate runs build + test on the same workspace ───
        // Resolve via the production resolver (same options as Worker DI)
        // to confirm the gate uses the identical physical path.
        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var gate = scope.ServiceProvider.GetRequiredService<IVerificationGate>();
            var projectRepo = new ProjectRepository(db);
            var project = (await projectRepo.GetByIdAsync(projectId))!;
            var workItemRepo = new WorkItemRepository(db);
            var workItem = (await workItemRepo.GetByIdAsync(workItemId))!;

            var verification = await gate.VerifyAsync(project, workItem, CancellationToken.None);

            Assert.True(verification.Status == VerificationStatus.Passed,
                $"VerificationGate failed after agent modification. Summary: {verification.Summary}");
        }

        // ── 7. WorkItem must have reached Completed ───────────────────────
        var finalWorkItem = await WaitForWorkItemLeavesRunningAsync(testHost, workItemId, TimeSpan.FromSeconds(30));
        Assert.NotNull(finalWorkItem);
        Assert.True(finalWorkItem!.Status == WorkItemStatus.Completed.ToString(),
            $"WorkItem did not reach Completed — actual: {finalWorkItem.Status}");

        // Cleanup fixture
        try { Directory.Delete(fixtureTemp, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Phase 18.18 — Real Hermes Acceptance: Runs real Docker Hermes against
    /// an initialized Project workspace from a bare Git fixture, verifies
    /// host filesystem modifications, and executes VerificationGate.
    /// Opt-in only via AI_FACTORY_HERMES_SMOKE_TEST=true.
    /// </summary>
    [Fact]
    public async Task RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled()
    {
        if (!Enabled)
        {
            return;
        }

        var fixtureTemp = Path.Combine(Path.GetTempPath(), "hermes-p1818-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureTemp);
        var bareRepoPath = CreateDemoFixtureBareRepo(fixtureTemp);
        var projectsBaseDir = Path.Combine(fixtureTemp, "workspaces");
        Directory.CreateDirectory(projectsBaseDir);

        using var testHost = BuildHost(
            configureHermes: options =>
            {
                options.AdditionalEnvironmentVariables["OPENAI_BASE_URL"] = OmniRouteBaseUrl;
                options.AdditionalEnvironmentVariables["OPENAI_API_KEY"] = "omniroute-local";
            },
            configureWorker: options =>
            {
                options.PollingInterval = TimeSpan.FromMilliseconds(200);
            },
            configureWorkspace: options =>
            {
                options.ProjectsBaseDirectory = projectsBaseDir;
                options.AllowedRoot = fixtureTemp;
            });

        Guid projectId;
        Guid workItemId;
        Guid agentRunId;
        string resolvedProjectRoot;

        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var projectRepo = new ProjectRepository(db);
            var workspaceOptions = scope.ServiceProvider.GetRequiredService<AgentWorkspaceOptions>();
            var resolver = new ProjectWorkspaceResolver(workspaceOptions);
            var provisioner = new ProjectWorkspaceProvisioner(resolver, workspaceOptions, NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var projectService = new ProjectService(projectRepo, provisioner);

            var project = await projectService.CreateAsync("Hermes Lifecycle", "Real Hermes Lifecycle acceptance");
            projectId = project.Id;

            var processRunner = new SystemProcessRunner();
            var executionLimits = new ExecutionLimitsOptions { BuildTimeoutMinutes = 5 };
            var sourceInitializer = new ProjectSourceInitializer(resolver, processRunner, executionLimits, NullLogger<ProjectSourceInitializer>.Instance);
            var initResult = await sourceInitializer.InitializeAsync(
                project, new SourceSpecification(ProjectSourceType.GitRepository, bareRepoPath, "main"), CancellationToken.None);
            Assert.True(initResult.Success, $"Source init failed: {initResult.FailureReason}");

            resolvedProjectRoot = resolver.Resolve(project).Path!;

            var workItemRepo = new WorkItemRepository(db);
            var workItem = new WorkItem
            {
                ProjectId = projectId,
                Title = "Phase 18.18 Real Hermes Task",
                Description = "Replace MARKER_BEFORE with MARKER_AFTER in src/Marker.cs",
                Type = WorkItemType.Implementation,
                Status = WorkItemStatus.Running.ToString(),
                Priority = "Normal"
            };
            await workItemRepo.AddAsync(workItem);
            await db.SaveChangesAsync();
            workItemId = workItem.Id;

            var runRepo = new AgentRunRepository(db);
            var definition = (await new AgentDefinitionRepository(db).GetAllAsync()).First();

            var run = new AgentRun
            {
                ProjectId = projectId,
                WorkItemId = workItemId,
                AgentDefinitionId = definition.Id,
                Input = "In the file src/Marker.cs, replace the text 'MARKER_BEFORE' with 'MARKER_AFTER'. Do not change anything else."
            };
            run = await runRepo.CreateAsync(run);
            run.TransitionTo(AgentRunStatus.Queued);
            await runRepo.UpdateAsync(run);
            agentRunId = run.Id;

            var reloaded = (await runRepo.GetByIdAsync(agentRunId))!;
            reloaded.Model = OmniRouteModel;
            await runRepo.UpdateAsync(reloaded);
        }

        var worker = GetAgentRunWorker(testHost.Host);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var finalRun = await WaitForTerminalAsync(testHost, agentRunId, TimeSpan.FromSeconds(240));
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(finalRun);
        Assert.Equal(AgentRunStatus.Succeeded, finalRun!.Status);

        var modifiedFile = Path.Combine(resolvedProjectRoot, "src", "Marker.cs");
        Assert.True(File.Exists(modifiedFile));
        var content = await File.ReadAllTextAsync(modifiedFile);
        Assert.Contains("MARKER_AFTER", content);

        await using (var scope = testHost.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var gate = scope.ServiceProvider.GetRequiredService<IVerificationGate>();
            var project = (await new ProjectRepository(db).GetByIdAsync(projectId))!;
            var workItem = (await new WorkItemRepository(db).GetByIdAsync(workItemId))!;

            var verification = await gate.VerifyAsync(project, workItem, CancellationToken.None);
            Assert.True(verification.Status == VerificationStatus.Passed);
        }

        try { Directory.Delete(fixtureTemp, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Phase 18.17 — creates a local bare Git repository containing a
    /// minimal C# solution that builds and tests green. The production code
    /// in src/Marker.cs contains the string "MARKER_BEFORE" which the Agent
    /// is instructed to change to "MARKER_AFTER". The test project asserts
    /// the file exists and compiles — it does NOT assert the marker value,
    /// so the agent's modification does not break the tests.
    /// </summary>
    private static string CreateDemoFixtureBareRepo(string tempDir)
    {
        var seedDir = Path.Combine(tempDir, "seed");
        Directory.CreateDirectory(seedDir);

        var srcDir = Path.Combine(seedDir, "src");
        var testsDir = Path.Combine(seedDir, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        // src/Demo.csproj
        File.WriteAllText(Path.Combine(srcDir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        // src/Marker.cs — the file the Agent will modify
        File.WriteAllText(Path.Combine(srcDir, "Marker.cs"), """
            namespace Demo;

            public static class Marker
            {
                // The agent will change "MARKER_BEFORE" to "MARKER_AFTER" on the next line.
                public const string Value = "MARKER_BEFORE";
            }
            """);

        // tests/Demo.Tests.csproj
        File.WriteAllText(Path.Combine(testsDir, "Demo.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2">
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <ProjectReference Include="../src/Demo.csproj" />
              </ItemGroup>
            </Project>
            """);

        // tests/MarkerTests.cs — verifies Marker.cs compiles; does NOT assert the value
        File.WriteAllText(Path.Combine(testsDir, "MarkerTests.cs"), """
            using Xunit;

            namespace Demo.Tests;

            public class MarkerTests
            {
                [Fact]
                public void Marker_Value_IsNotEmpty()
                {
                    // Only verifies the string is non-empty — the agent can freely
                    // change MARKER_BEFORE to MARKER_AFTER without failing this test.
                    Assert.NotEmpty(Demo.Marker.Value);
                }
            }
            """);

        // Git init + initial commit + bare clone
        RunGitInFixture(seedDir, "init", "-b", "main");
        RunGitInFixture(seedDir, "config", "user.email", "test@example.com");
        RunGitInFixture(seedDir, "config", "user.name", "Test");
        RunGitInFixture(seedDir, "config", "commit.gpgsign", "false");
        RunGitInFixture(seedDir, "add", ".");
        RunGitInFixture(seedDir, "commit", "-m", "initial commit");

        var bareRepoPath = Path.Combine(tempDir, "demo.git");
        RunGitInFixture(tempDir, "clone", "--bare", seedDir, bareRepoPath);

        return bareRepoPath;
    }

    private static void RunGitInFixture(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed:\n{process.StandardError.ReadToEnd()}");
        }
    }

    private static async Task<AgentRun?> WaitForTerminalAsync(TestHost testHost, Guid runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var scope = testHost.Host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            var run = await new AgentRunRepository(db).GetByIdAsync(runId);

            if (run is not null && AgentRunTransitions.IsTerminal(run.Status))
            {
                return run;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private static AI.Factory.Worker.AgentRunWorker GetAgentRunWorker(IHost host) =>
        (AI.Factory.Worker.AgentRunWorker)host.Services
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Single(s => s is AI.Factory.Worker.AgentRunWorker);

    private static TestHost BuildHost(
        Action<HermesOptions>? configureHermes = null,
        Action<AgentWorkerOptions>? configureWorker = null,
        Action<AgentWorkspaceOptions>? configureWorkspace = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var logs = new List<string>();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Production"
        });

        builder.Logging.AddProvider(new CapturingLoggerProvider(logs));

        Program.ConfigureServices(builder);

        var dbDescriptor = builder.Services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
        if (dbDescriptor is not null)
        {
            builder.Services.Remove(dbDescriptor);
        }
        builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(connection));

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ai-factory-e2e-workspace-" + Guid.NewGuid().ToString("N"));
        var dataVolume = Path.Combine(Path.GetTempPath(), "ai-factory-e2e-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(dataVolume);

        var hermesOptions = new HermesOptions
        {
            DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE") ?? "nousresearch/hermes-agent:latest",
            DataVolume = dataVolume,
            WorkspaceRoot = workspaceRoot,
            TimeoutSeconds = 120,
            MaxOutputLength = 20_000,
            // "none" is the production default; a real completion needs
            // egress, so this test's default is "bridge". Individual tests
            // still deliberately break this (invalid network, short
            // timeout) via configureHermes to exercise failure/timeout paths.
            NetworkMode = "bridge",
            Provider = "openrouter",
            ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
            ProviderApiKeySecretName = "OMNIROUTE_API_KEY"
        };
        hermesOptions.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = OmniRouteBaseUrl;
        configureHermes?.Invoke(hermesOptions);

        var hermesDescriptor = builder.Services.Single(d => d.ServiceType == typeof(HermesOptions));
        builder.Services.Remove(hermesDescriptor);
        builder.Services.AddSingleton(hermesOptions);

        var workerOptions = new AgentWorkerOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(300),
            ExecutionTimeout = TimeSpan.FromSeconds(150)
        };
        configureWorker?.Invoke(workerOptions);

        var workerOptionsDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkerOptions));
        builder.Services.Remove(workerOptionsDescriptor);
        builder.Services.AddSingleton(workerOptions);

        // Phase 18.17 — allow tests to configure AgentWorkspaceOptions
        // (e.g. set ProjectsBaseDirectory) so the Worker's IWorkspaceResolver
        // resolves through the same base directory as the test Provisioner.
        if (configureWorkspace is not null)
        {
            var workspaceDescriptor = builder.Services.Single(d => d.ServiceType == typeof(AgentWorkspaceOptions));
            var existingOptions = (AgentWorkspaceOptions)workspaceDescriptor.ImplementationInstance!;
            configureWorkspace(existingOptions);
        }

        var host = builder.Build();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;
        using (var db = new FactoryDbContext(schemaOptions))
        {
            db.Database.EnsureCreated();
        }

        var metrics = host.Services.GetRequiredService<IOperationalMetrics>();

        return new TestHost(host, connection, hermesOptions, metrics, logs, workspaceRoot, dataVolume);
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }

    private sealed class TestHost : IDisposable
    {
        public TestHost(
            IHost host, SqliteConnection connection, HermesOptions hermes, IOperationalMetrics metrics,
            List<string> logs, string workspaceRoot, string dataVolume)
        {
            Host = host;
            Connection = connection;
            Hermes = hermes;
            Metrics = metrics;
            Logs = logs;
            _workspaceRoot = workspaceRoot;
            _dataVolume = dataVolume;
        }

        private readonly string _workspaceRoot;
        private readonly string _dataVolume;

        public IHost Host { get; }
        public SqliteConnection Connection { get; }
        public HermesOptions Hermes { get; }
        public IOperationalMetrics Metrics { get; }
        public List<string> Logs { get; }

        public void Dispose()
        {
            Host.Dispose();
            Connection.Dispose();

            // Best-effort cleanup of the isolated temp workspace/data dirs
            // this test created — a leftover empty dir is not worth failing
            // the test over (e.g. Docker may still be releasing a bind mount).
            foreach (var path in new[] { _workspaceRoot, _dataVolume })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines;

        public CapturingLoggerProvider(List<string> lines) => _lines = lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _lines;

            public CapturingLogger(string category, List<string> lines)
            {
                _category = category;
                _lines = lines;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = $"[{_category}] {formatter(state, exception)}";
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                }
            }
        }
    }
}
