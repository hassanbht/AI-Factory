using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
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
        IAgentRunRepository repository, IAgentOrchestrator orchestrator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(orchestrator);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static AI.Factory.Worker.AgentRunWorker CreateWorker(
        IAgentRunRepository repository,
        IAgentOrchestrator orchestrator,
        IApplicationShutdown shutdown,
        BackgroundOperationTracker tracker,
        OperationalMetrics metrics,
        AgentWorkerOptions? options = null,
        IWorkerHealthCoordinator? coordinator = null)
    {
        return new AI.Factory.Worker.AgentRunWorker(
            BuildScopeFactory(repository, orchestrator),
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
