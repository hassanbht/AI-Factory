using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Diagnostics;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.1.5 — AgentRunWorker claim/execute/failure-isolation/shutdown
/// coverage. Mirrors WorkerRestartRecoveryTests' pattern for OrchestrationRunWorker:
/// hand-rolled fakes, real Stopwatch-bounded polling loops, no fixed sleeps.
/// </summary>
public sealed class AgentRunWorkerTests
{
    private static IServiceScopeFactory BuildScopeFactory(
        IAgentRunRepository repository,
        IAgentOrchestrator orchestrator,
        ITaskStateManager? taskStateManager = null,
        IAgentRunWorkItemSync? sync = null,
        IProjectPlanner? planner = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(orchestrator);
        services.AddSingleton(taskStateManager ?? new FakeTaskStateManager());
        services.AddSingleton(sync ?? new FakeAgentRunWorkItemSync());
        services.AddSingleton(planner ?? new FakeProjectPlanner());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static AI.Factory.Worker.AgentRunWorker CreateWorker(
        IAgentRunRepository repository,
        IAgentOrchestrator orchestrator,
        IApplicationShutdown shutdown,
        BackgroundOperationTracker tracker,
        OperationalMetrics metrics,
        AgentWorkerOptions? options = null,
        IWorkerHealthCoordinator? coordinator = null,
        ITaskStateManager? taskStateManager = null,
        IAgentRunWorkItemSync? sync = null,
        IProjectPlanner? planner = null)
    {
        return new AI.Factory.Worker.AgentRunWorker(
            BuildScopeFactory(repository, orchestrator, taskStateManager, sync, planner),
            options ?? new AgentWorkerOptions { PollingInterval = TimeSpan.FromMilliseconds(50) },
            shutdown,
            tracker,
            metrics,
            NullLogger<AI.Factory.Worker.AgentRunWorker>.Instance,
            coordinator);
    }

    private static AgentRun NewRun() => new()
    {
        Id = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        Status = AgentRunStatus.Running
    };

    private static AgentRun NewRunWithWorkItem(Guid workItemId) => new()
    {
        Id = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        WorkItemId = workItemId,
        Status = AgentRunStatus.Running
    };

    private static AgentRun NewProjectLevelRun(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        ProjectId = projectId,
        WorkItemId = null,
        Status = AgentRunStatus.Running
    };

    private static AgentOrchestrationResult SuccessResult(Guid correlationId) => new(
        Executed: true, Success: true, FinalStatus: AgentRunStatus.Succeeded, Output: "ok", DurationMs: 5,
        ToolCallNames: Array.Empty<string>(), AvailableToolNames: Array.Empty<string>(),
        ErrorMessage: null, FailureCategory: null, CorrelationId: correlationId);

    [Fact]
    public async Task Worker_ClaimsQueuedAgentRun_AndPassesItToOrchestrator()
    {
        var run = NewRun();
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator((id, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.ExecutedRunIds.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Single(orchestrator.ExecutedRunIds);
        Assert.Equal(run.Id, orchestrator.ExecutedRunIds[0]);
    }

    [Fact]
    public async Task Worker_SuccessfulExecution_RecordsClaimSuccessAndSucceededMetrics()
    {
        var run = NewRun();
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator((id, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.ExecutedRunIds.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.AgentWorkerClaimSuccess);
        Assert.Equal(1, snapshot.AgentRunsStarted);
        Assert.Equal(1, snapshot.AgentRunsSucceeded);
        Assert.Equal(0, snapshot.AgentRunsFailed);
    }

    [Fact]
    public async Task Worker_SkippedExecution_DoesNotRecordLifecycleMetrics()
    {
        var run = NewRun();
        var repository = new FakeAgentRunRepository(new[] { run });
        var skippedResult = new AgentOrchestrationResult(
            Executed: false, Success: false, FinalStatus: AgentRunStatus.Running, Output: null, DurationMs: 0,
            ToolCallNames: Array.Empty<string>(), AvailableToolNames: Array.Empty<string>(),
            ErrorMessage: "already running", FailureCategory: null, CorrelationId: Guid.NewGuid());
        var orchestrator = new FakeAgentOrchestrator((_, _) => Task.FromResult(skippedResult));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.ExecutedRunIds.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.AgentWorkerClaimSuccess);
        Assert.Equal(0, snapshot.AgentRunsStarted);
    }

    [Fact]
    public async Task Worker_Idle_NeverCallsOrchestrator_WhenNothingIsQueued()
    {
        var repository = new FakeAgentRunRepository(Array.Empty<AgentRun>());
        var orchestrator = new FakeAgentOrchestrator((_, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(orchestrator.ExecutedRunIds);
        Assert.True(repository.TryClaimCalls > 0, "Worker never polled.");
    }

    [Fact]
    public async Task Worker_SurvivesOrchestratorException_AndKeepsPolling()
    {
        var run = NewRun();
        var repository = new FakeAgentRunRepository(new[] { run, run, run });
        var callCount = 0;
        var orchestrator = new FakeAgentOrchestrator((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("Simulated orchestrator failure.");
            }
            return Task.FromResult(SuccessResult(Guid.NewGuid()));
        });

        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.CallLog.Count < 2 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        // The first orchestrator call threw; the loop must have kept polling
        // and reached at least a second attempt instead of dying.
        Assert.True(orchestrator.CallLog.Count >= 2, "Worker did not survive a single failed AgentRun.");
    }

    [Fact]
    public async Task StopAsync_CompletesCleanly_WithNoUnobservedExceptions()
    {
        var repository = new FakeAgentRunRepository(Array.Empty<AgentRun>());
        var orchestrator = new FakeAgentOrchestrator((_, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(60);

        // If StopAsync throws, this test fails outright — no try/catch here on purpose.
        await worker.StopAsync(CancellationToken.None);

        Assert.True(shutdown.IsStopping);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task Worker_DoesNotSpinAggressively_WhenIdle()
    {
        var repository = new FakeAgentRunRepository(Array.Empty<AgentRun>());
        var orchestrator = new FakeAgentOrchestrator((_, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions { PollingInterval = TimeSpan.FromMilliseconds(50) });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        // 300ms at a 50ms interval is at most ~6 ticks; a tight/unthrottled
        // loop would produce orders of magnitude more.
        Assert.True(repository.TryClaimCalls < 20, $"Worker polled {repository.TryClaimCalls} times — looks unthrottled.");
    }

    // ---------------- Phase 18.6 — Planner materialization ----------------

    [Fact]
    public async Task Worker_TerminalProjectLevelAgentRun_InvokesPlannerMaterialize()
    {
        var projectId = Guid.NewGuid();
        var run = NewProjectLevelRun(projectId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator((id, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        var planner = new FakeProjectPlanner();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics, planner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (planner.MaterializedRunIds.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Single(planner.MaterializedRunIds);
        Assert.Equal(run.Id, planner.MaterializedRunIds[0]);
    }

    [Fact]
    public async Task Worker_TerminalWorkItemLinkedAgentRun_NeverInvokesPlannerMaterialize()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator((id, _) => Task.FromResult(SuccessResult(Guid.NewGuid())));
        var planner = new FakeProjectPlanner();
        var sync = new FakeAgentRunWorkItemSync();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(repository, orchestrator, shutdown, tracker, metrics, sync: sync, planner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.ExecutedRunIds.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(planner.MaterializedRunIds);
    }

    // ---------------- Phase 18.4 — heartbeat ----------------

    [Fact]
    public async Task Heartbeat_DuringLongRunningExecution_AdvancesLastHeartbeat()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            return SuccessResult(Guid.NewGuid());
        });
        var taskStateManager = new FakeTaskStateManager();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(20)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (taskStateManager.CallCountFor(workItemId) < 2 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.True(
            taskStateManager.CallCountFor(workItemId) >= 2,
            $"Expected at least 2 heartbeat calls, got {taskStateManager.CallCountFor(workItemId)}.");
    }

    [Fact]
    public async Task Heartbeat_StopsAfterSuccessfulExecution()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60), ct);
            return SuccessResult(Guid.NewGuid());
        });
        var taskStateManager = new FakeTaskStateManager();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(15)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (metrics.GetSnapshot().AgentRunsSucceeded < 1 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, metrics.GetSnapshot().AgentRunsSucceeded);

        await worker.StopAsync(CancellationToken.None);

        var countAfterCompletion = taskStateManager.CallCountFor(workItemId);
        await Task.Delay(100);

        Assert.Equal(countAfterCompletion, taskStateManager.CallCountFor(workItemId));
    }

    [Fact]
    public async Task Heartbeat_StopsAfterOrchestratorException()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(40), ct);
            throw new InvalidOperationException("Simulated orchestrator failure.");
        });
        var taskStateManager = new FakeTaskStateManager();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(10)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.CallLog.Count == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        // Give the (thrown) attempt time to actually land, then stop.
        await Task.Delay(80);
        await worker.StopAsync(CancellationToken.None);

        var countAfterFailure = taskStateManager.CallCountFor(workItemId);
        await Task.Delay(100);

        Assert.Equal(countAfterFailure, taskStateManager.CallCountFor(workItemId));
    }

    [Fact]
    public async Task Heartbeat_StopsOnExecutionTimeout()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            // Ignores the request to finish quickly — only the configured
            // ExecutionTimeout (via executionCts) will ever stop this.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return SuccessResult(Guid.NewGuid());
        });
        var taskStateManager = new FakeTaskStateManager();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                ExecutionTimeout = TimeSpan.FromMilliseconds(80),
                HeartbeatInterval = TimeSpan.FromMilliseconds(15)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (orchestrator.CallLog.Count == 0 && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(20);
        }

        // Let the timeout fire and PollOnceAsync's finally run.
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        var countAfterTimeout = taskStateManager.CallCountFor(workItemId);
        await Task.Delay(100);

        Assert.Equal(countAfterTimeout, taskStateManager.CallCountFor(workItemId));
        Assert.True(countAfterTimeout > 0, "Expected at least one heartbeat before the timeout fired.");
    }

    [Fact]
    public async Task Heartbeat_FailureDoesNotStopExecution_AndIsObservable()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), ct);
            return SuccessResult(Guid.NewGuid());
        });
        var taskStateManager = new FakeTaskStateManager(alwaysFail: true);
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(15)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (metrics.GetSnapshot().AgentRunsSucceeded < 1 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        // The execution itself must succeed despite every heartbeat attempt failing.
        Assert.Single(orchestrator.ExecutedRunIds);
        Assert.Equal(1, metrics.GetSnapshot().AgentRunsSucceeded);
        Assert.True(metrics.GetSnapshot().AgentRunHeartbeatFailures > 0, "Heartbeat failures were not recorded.");
    }

    [Fact]
    public async Task Heartbeat_MultipleAgentRuns_EachHeartbeatsIndependently()
    {
        var workItemIdA = Guid.NewGuid();
        var workItemIdB = Guid.NewGuid();
        var runA = NewRunWithWorkItem(workItemIdA);
        var runB = NewRunWithWorkItem(workItemIdB);
        var repository = new FakeAgentRunRepository(new[] { runA, runB });
        var taskStateManager = new FakeTaskStateManager();
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            var targetId = id == runA.Id ? workItemIdA : workItemIdB;
            var spin = Stopwatch.StartNew();
            while (taskStateManager.CallCountFor(targetId) == 0 && spin.ElapsedMilliseconds < 1000 && !ct.IsCancellationRequested)
            {
                await Task.Delay(5, ct);
            }
            return SuccessResult(Guid.NewGuid());
        });
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(15)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (metrics.GetSnapshot().AgentRunsSucceeded < 2 && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.True(taskStateManager.CallCountFor(workItemIdA) > 0, "WorkItem A never received a heartbeat.");
        Assert.True(taskStateManager.CallCountFor(workItemIdB) > 0, "WorkItem B never received a heartbeat.");
    }

    [Fact]
    public async Task Worker_Shutdown_StopsHeartbeatLoop_NoFurtherHeartbeatsAfterStop()
    {
        var workItemId = Guid.NewGuid();
        var run = NewRunWithWorkItem(workItemId);
        var repository = new FakeAgentRunRepository(new[] { run });
        var orchestrator = new FakeAgentOrchestrator(async (id, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return SuccessResult(Guid.NewGuid());
        });
        var taskStateManager = new FakeTaskStateManager();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            repository, orchestrator, shutdown, tracker, metrics,
            options: new AgentWorkerOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(15)
            },
            taskStateManager: taskStateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        while (taskStateManager.CallCountFor(workItemId) == 0 && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        var countAfterStop = taskStateManager.CallCountFor(workItemId);
        await Task.Delay(150);

        Assert.Equal(countAfterStop, taskStateManager.CallCountFor(workItemId));
        Assert.Equal(0, tracker.ActiveCount);
    }

    private sealed class FakeTaskStateManager : ITaskStateManager
    {
        private readonly bool _alwaysFail;

        public FakeTaskStateManager(bool alwaysFail = false)
        {
            _alwaysFail = alwaysFail;
        }

        private readonly List<(Guid WorkItemId, DateTime AtUtc)> _heartbeatCalls = new();

        public int CallCountFor(Guid workItemId)
        {
            lock (_heartbeatCalls)
            {
                return _heartbeatCalls.Count(x => x.WorkItemId == workItemId);
            }
        }

        public Task<bool> TryStartAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task CompleteAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> FailAsync(Guid workItemId, string error, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecoverOrphanedTasksAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task HeartbeatAsync(
            Guid workItemId, string? checkpoint = null, CancellationToken cancellationToken = default)
        {
            if (_alwaysFail)
            {
                throw new InvalidOperationException("Simulated transient heartbeat failure.");
            }

            lock (_heartbeatCalls)
            {
                _heartbeatCalls.Add((workItemId, DateTime.UtcNow));
            }

            return Task.CompletedTask;
        }

        public Task<int> RecoverStaleExecutionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeAgentRunWorkItemSync : IAgentRunWorkItemSync
    {
        public Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProjectPlanner : IProjectPlanner
    {
        public List<Guid> MaterializedRunIds { get; } = new();

        public Task<AgentRun> GenerateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentRun?> GetLatestRunAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRun?>(null);

        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());

        public Task<PlannerMaterializationResult> MaterializeAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            MaterializedRunIds.Add(agentRunId);
            return Task.FromResult(new PlannerMaterializationResult(PlannerMaterializationOutcome.Skipped, null, "fake"));
        }
    }

    private sealed class FakeAgentRunRepository : IAgentRunRepository
    {
        private readonly Queue<AgentRun> _pending;

        public FakeAgentRunRepository(IEnumerable<AgentRun> pending)
        {
            _pending = new Queue<AgentRun>(pending);
        }

        public int TryClaimCalls { get; private set; }

        public Task<AgentRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            TryClaimCalls++;
            return Task.FromResult(_pending.Count > 0 ? _pending.Dequeue() : null);
        }

        public Task<AgentRun> CreateAsync(AgentRun agentRun, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentRun?> GetByIdAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentRun?> GetByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateAsync(AgentRun agentRun, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentRunPage> GetPagedAsync(AgentRunQuery query, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> CountByStatusAsync(AgentRunStatus status, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> HasActiveRunsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task HeartbeatAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> RecoverOrphanedRunsAsync(
            DateTime? startedBeforeUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class FakeAgentOrchestrator : IAgentOrchestrator
    {
        private readonly Func<Guid, CancellationToken, Task<AgentOrchestrationResult>> _behavior;

        public FakeAgentOrchestrator(Func<Guid, CancellationToken, Task<AgentOrchestrationResult>> behavior)
        {
            _behavior = behavior;
        }

        public List<Guid> ExecutedRunIds { get; } = new();

        public List<Guid> CallLog { get; } = new();

        public Task<AgentOrchestrationResult> ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken = default)
        {
            CallLog.Add(agentRunId);
            var result = _behavior(agentRunId, cancellationToken);
            ExecutedRunIds.Add(agentRunId);
            return result;
        }
    }
}
