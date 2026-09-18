using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Planning;
using AI.Factory.Application.Projects;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.26 §5/§6/§8 — Worker restart proven against the REAL live
/// lifecycle, not only deterministically.
///
/// Phase 18.25 proved resume-from-persisted-state with fakes. This test
/// interrupts a Worker while a real Hermes-backed AgentRun is genuinely
/// executing, brings up a second Worker composition root over the same
/// database and workspace, and asserts from persisted data that the lifecycle
/// recovered without duplicating authoritative work.
///
/// The defect this test was written against: nothing recovered AgentRuns. A
/// Worker that died mid-run left its AgentRun Running forever, and because
/// IProjectCompletionGate blocks on HasActiveRunsForProjectAsync, the owning
/// Project could never reach HumanReview again.
///
/// Opt-in, like every real-Hermes test:
/// <code>
/// AI_FACTORY_HERMES_SMOKE_TEST=true dotnet test \
///   --filter FullyQualifiedName~LiveWorkerRecoveryE2ETests
/// </code>
/// </summary>
public sealed class LiveWorkerRecoveryE2ETests
{
    private const string RecoveryProjectDescription =
        "This repository is a small .NET solution (Demo.slnx) whose only source file is src/GreetingService.cs, " +
        "containing a GreetingService class whose GetGreeting() method currently returns the string Hello. " +
        "Edit src/GreetingService.cs so that GetGreeting() returns exactly: Hello, AI Factory! " +
        "Do not create or delete any other file.";

    private readonly ITestOutputHelper _output;

    public LiveWorkerRecoveryE2ETests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WorkerInterruptedMidAgentRun_RecoversOnRestart_WithoutDuplicatingWork()
    {
        if (!LiveWorkerHost.Enabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var cancellationToken = cts.Token;

        using var live = LiveWorkerHost.Create();

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(
                scope.ServiceProvider.GetRequiredService<FactoryDbContext>(), cancellationToken);
        }

        // ── Start a real Project through the one authoritative start path ──
        Guid projectId;

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var project = await projects.CreateAsync("Live Worker Recovery", RecoveryProjectDescription, cancellationToken);
            projectId = project.Id;

            await projects.InitializeSourceAsync(
                projectId,
                new SourceSpecification(ProjectSourceType.GitRepository, live.BareRepoPath, "main"),
                cancellationToken);

            await scope.ServiceProvider.GetRequiredService<IProjectLifecycleOrchestrator>()
                .StartAsync(projectId, "qa-lead@factory.local", cancellationToken);
        }

        var worker = live.Host.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();
        var scheduler = live.Host.Services.GetServices<IHostedService>()
            .OfType<TaskSchedulerHostedService>().Single();

        await worker.StartAsync(cancellationToken);
        await scheduler.StartAsync(cancellationToken);

        // ── Wait until a real AgentRun is genuinely executing ──
        var beforeStop = await WaitForRunningAgentRunAsync(
            live, projectId, TimeSpan.FromMinutes(8), cancellationToken);

        var beforeProjectStatus = await ReadProjectStatusAsync(live, projectId, cancellationToken);

        _output.WriteLine("── Before Worker stop ──");
        _output.WriteLine($"ProjectId      = {projectId}");
        _output.WriteLine($"Project Status = {beforeProjectStatus}");
        _output.WriteLine($"AgentRunId     = {beforeStop.Id}");
        _output.WriteLine($"AgentRun Status= {beforeStop.Status}");
        _output.WriteLine($"AgentRun Model = {beforeStop.Model ?? "(unresolved)"}");

        // ── Interrupt the Worker mid-execution ──
        // StopAsync(already-cancelled token) is the closest faithful in-process
        // stand-in for process death: the hosted services stop without being
        // given the chance to finish or terminalize the in-flight AgentRun,
        // which is exactly the state a crash leaves behind.
        using (var abrupt = new CancellationTokenSource())
        {
            await abrupt.CancelAsync();

            try
            {
                await scheduler.StopAsync(abrupt.Token);
                await worker.StopAsync(abrupt.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected, and the whole point: the shutdown drain is
                // abandoned rather than allowed to complete, so the in-flight
                // AgentRun is never terminalized by the dying Worker. A real
                // process kill leaves exactly this state behind.
            }
        }

        _output.WriteLine("── Worker stopped ──");

        var orphanCandidate = await ReadAgentRunAsync(live, beforeStop.Id, cancellationToken);
        _output.WriteLine($"AgentRun after stop = {orphanCandidate.Status}");

        // ── Restart: a brand new composition root over the same persisted state ──
        using var restarted = live.BuildRestartedWorkerHost();

        var recovery = restarted.Services.GetServices<IHostedService>()
            .OfType<WorkflowRecoveryHostedService>().Single();

        await recovery.StartAsync(cancellationToken);

        _output.WriteLine("── Worker restarted ──");

        // §7 — the orphaned run must not stay active, or the completion gate
        // stays blocked forever.
        var afterRecovery = await WaitForTerminalAgentRunAsync(
            live, beforeStop.Id, TimeSpan.FromMinutes(3), cancellationToken);

        _output.WriteLine("── After recovery ──");
        _output.WriteLine($"same ProjectId       = {projectId}");
        _output.WriteLine(
            $"recovery decision    = {(AgentRunRecovery.WasOrphaned(afterRecovery) ? "RESUMED EXISTING WORK (orphan terminalized; no new authoritative run created by recovery itself)" : "not an orphan recovery")}");
        _output.WriteLine($"AgentRun outcome     = {afterRecovery.Status} / {afterRecovery.FailureCategory}");

        Assert.True(
            AgentRunTransitions.IsTerminal(afterRecovery.Status),
            $"The orphaned AgentRun is still {afterRecovery.Status}; the Project would be blocked forever.");

        Assert.True(
            AgentRunRecovery.WasOrphaned(afterRecovery),
            "The orphaned AgentRun was not marked as recovered by orphan recovery.");

        Assert.Equal(FailureCategory.Infrastructure, afterRecovery.FailureCategory);

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            Assert.False(
                await scope.ServiceProvider.GetRequiredService<IAgentRunRepository>()
                    .HasActiveRunsForProjectAsync(projectId, cancellationToken),
                "A recovered Project must have no active AgentRuns left blocking its completion gate.");
        }

        // ── Resume under the restarted Worker ──
        var restartedWorker = restarted.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();
        var restartedScheduler = restarted.Services.GetServices<IHostedService>()
            .OfType<TaskSchedulerHostedService>().Single();

        await restartedWorker.StartAsync(cancellationToken);
        await restartedScheduler.StartAsync(cancellationToken);

        try
        {
            // §6/§7 — the lifecycle must move again on its own, and must never
            // run two Planners for one Project.
            var resumed = await WaitForLifecycleProgressAsync(
                live, projectId, beforeProjectStatus, TimeSpan.FromMinutes(20), cancellationToken);

            _output.WriteLine($"Project outcome      = {resumed}");

            var plannerRuns = await ReadPlannerRunsAsync(live, projectId, cancellationToken);
            _output.WriteLine($"duplicate check      = {plannerRuns.Count} Planner AgentRun(s)");

            foreach (var run in plannerRuns)
            {
                _output.WriteLine($"  Planner {run.Id} status={run.Status} model={run.Model ?? "(unresolved)"}");
            }

            // At most one Planner per Project, plus at most the single
            // crash-scoped retry ProjectLifecycleOrchestrator permits.
            Assert.True(
                plannerRuns.Count <= ProjectLifecycleOrchestrator.MaxPlannerCrashRetries + 1,
                $"{plannerRuns.Count} Planner AgentRuns exist for one Project — the crash retry is not bounded.");

            await AssertNoDuplicateWorkItemPlanKeysAsync(live, projectId, cancellationToken);
        }
        finally
        {
            await restartedScheduler.StopAsync(CancellationToken.None);
            await restartedWorker.StopAsync(CancellationToken.None);
            await recovery.StopAsync(CancellationToken.None);
        }
    }

    // ---------------- Observation helpers (persisted data only) ----------------

    private async Task<AgentRun> WaitForRunningAgentRunAsync(
        LiveWorkerHost live, Guid projectId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var page = await scope.ServiceProvider.GetRequiredService<IAgentRunRepository>()
                    .GetPagedAsync(new AgentRunQuery(ProjectId: projectId, Page: 1, PageSize: 50), cancellationToken);

                var running = page.Items.FirstOrDefault(r => r.Status == AgentRunStatus.Running);

                if (running is not null)
                {
                    return running;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"No AgentRun for project {projectId} ever reached Running.");
    }

    private static async Task<AgentRun> ReadAgentRunAsync(
        LiveWorkerHost live, Guid agentRunId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IAgentRunRepository>()
            .GetByIdAsync(agentRunId, cancellationToken))!;
    }

    private static async Task<AgentRun> WaitForTerminalAgentRunAsync(
        LiveWorkerHost live, Guid agentRunId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        AgentRun? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await ReadAgentRunAsync(live, agentRunId, cancellationToken);

            if (AgentRunTransitions.IsTerminal(last.Status))
            {
                return last;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return last!;
    }

    private static async Task<ProjectStatus> ReadProjectStatusAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IProjectRepository>()
            .GetByIdAsync(projectId, cancellationToken))!.Status;
    }

    /// <summary>
    /// Recovery counts as "lifecycle resumed" when the Project either advances
    /// past where it was interrupted or reaches a terminal state on its own —
    /// what must never happen is staying frozen at the interruption point.
    /// </summary>
    private async Task<ProjectStatus> WaitForLifecycleProgressAsync(
        LiveWorkerHost live, Guid projectId, ProjectStatus interruptedAt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = interruptedAt;

        while (DateTime.UtcNow < deadline)
        {
            var current = await ReadProjectStatusAsync(live, projectId, cancellationToken);

            if (current != last)
            {
                _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Project {projectId} -> {current}");
                last = current;
            }

            if (current != interruptedAt)
            {
                return current;
            }

            await Task.Delay(2000, cancellationToken);
        }

        throw new TimeoutException(
            $"Project {projectId} never moved past {interruptedAt} after the Worker restart — the lifecycle did not resume.");
    }

    private static async Task<IReadOnlyList<AgentRun>> ReadPlannerRunsAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProjectPlanner>()
            .GetRunsAsync(projectId, cancellationToken);
    }

    private static async Task AssertNoDuplicateWorkItemPlanKeysAsync(
        LiveWorkerHost live, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();

        var items = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>()
            .GetByProjectIdAsync(projectId, cancellationToken);

        var duplicates = items
            .Where(i => i.PlanKey is not null)
            .GroupBy(i => i.PlanKey!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Recovery duplicated WorkItems for plan key(s): {string.Join(", ", duplicates)}.");
    }
}
