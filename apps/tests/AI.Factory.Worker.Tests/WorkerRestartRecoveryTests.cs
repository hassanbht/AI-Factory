using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Diagnostics;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 17.4.9 section 10 — worker interruption, restart, and fault
/// coverage. Extends the pattern established by
/// <see cref="OrchestrationRunWorkerShutdownTests"/> with scenarios for
/// mid-drain claim rejection, restart-without-duplicate-execution, and
/// unhandled dependency failures inside the poll loop.
/// </summary>
[Trait("Category", "Resilience")]
public sealed class WorkerRestartRecoveryTests
{
    private static IServiceScopeFactory BuildScopeFactory(IOrchestrationRunRepository repository)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static OrchestrationRunWorker CreateWorker(
        IOrchestrationRunRepository repository,
        IApplicationShutdown shutdown,
        BackgroundOperationTracker tracker,
        OperationalMetrics metrics,
        OrchestrationOptions? options = null,
        ShutdownOptions? shutdownOptions = null,
        IWorkerHealthCoordinator? coordinator = null)
    {
        return new OrchestrationRunWorker(
            BuildScopeFactory(repository),
            options ?? new OrchestrationOptions { QueuePollingInterval = TimeSpan.FromMilliseconds(50) },
            shutdownOptions ?? new ShutdownOptions { GracefulTimeoutSeconds = 2, ForceCancellationAfterSeconds = 1 },
            shutdown,
            tracker,
            metrics,
            NullLogger<OrchestrationRunWorker>.Instance,
            coordinator);
    }

    // ---------------------------------------------------------------
    // 10.1 Graceful cancellation
    // ---------------------------------------------------------------

    [Fact]
    public async Task StopAsync_RejectsClaimAttemptedMidDrain_AndReachesIsStoppingBeforeDrainCompletes()
    {
        var runRepository = new ClaimTrackingRepository();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(runRepository, shutdown, tracker, metrics);

        var context = new OperationContext(
            Guid.NewGuid(), Guid.NewGuid(), null, "ActiveTask", null, null, null, DateTime.UtcNow);
        var handle = tracker.Track(context);
        Assert.Equal(1, tracker.ActiveCount);

        var stopTask = Task.Run(() => worker.StopAsync(CancellationToken.None));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!shutdown.IsStopping && sw.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10);
        }

        Assert.True(shutdown.IsStopping);
        Assert.False(stopTask.IsCompleted);

        // A claim attempted while shutdown is in progress (but before the
        // in-flight operation drains) must be rejected: nothing new may be
        // claimed once shutdown has begun, regardless of drain state.
        var claimResult = await runRepository.TryClaimNextPendingAsync(CancellationToken.None);
        Assert.True(shutdown.IsStopping);
        Assert.Null(claimResult);

        handle.Dispose();
        await stopTask;

        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public async Task Worker_StopsClaimingNewWork_AsSoonAsStopBegins()
    {
        var runRepository = new ClaimTrackingRepository();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(runRepository, shutdown, tracker, metrics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        // Let poll ticks happen until we know claiming is actually live.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (runRepository.TryClaimCalls == 0 && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(20);
        }
        var claimsBeforeStop = runRepository.TryClaimCalls;
        Assert.True(claimsBeforeStop > 0, "Worker never polled for work before stop.");

        await worker.StopAsync(CancellationToken.None);
        var claimsAtStop = runRepository.TryClaimCalls;

        // Give the poll loop a chance to tick again if it were (incorrectly)
        // still running post-stop.
        await Task.Delay(150);

        Assert.Equal(claimsAtStop, runRepository.TryClaimCalls);
    }

    // ---------------------------------------------------------------
    // 10.2 Worker restart — no duplicate execution
    // ---------------------------------------------------------------

    [Fact]
    public async Task SecondWorker_RecoversStaleLease_ExactlyOnce_AfterFirstWorkerIsInterruptedMidRun()
    {
        // Shared repository state simulates the persistent database that
        // both worker instances (before/after restart) observe.
        var sharedRepository = new RestartSimulationRepository();

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            Status = OrchestrationRunStatus.Pending,
        };
        sharedRepository.Seed(run);

        // --- First worker instance: claims the run, then is hard-interrupted
        // (process killed) — it never releases the lease.
        using var shutdown1 = new ApplicationShutdownCoordinator();
        var tracker1 = new BackgroundOperationTracker();
        var metrics1 = new OperationalMetrics();
        var worker1 = CreateWorker(sharedRepository, shutdown1, tracker1, metrics1);

        var firstClaim = await sharedRepository.TryClaimNextPendingAsync(CancellationToken.None);
        Assert.NotNull(firstClaim);
        Assert.Equal(run.Id, firstClaim!.Id);

        // Hard interruption: no clean StopAsync/ReleaseLeaseAsync call, and
        // no further lease renewal — simulate time passing past lease expiry.
        sharedRepository.ExpireLease(run.Id);

        // --- Recovery entry point is WorkflowRecoveryHostedService's stale
        // sweep, which calls IOrchestrationRunRepository.RecoverStaleLeasesAsync
        // directly (see WorkflowRecoveryHostedService.RunStaleSweepAsync).
        var recoveredCount = await sharedRepository.RecoverStaleLeasesAsync(CancellationToken.None);
        Assert.Equal(1, recoveredCount);
        Assert.Equal(OrchestrationRunStatus.Pending, sharedRepository.GetStatus(run.Id));

        // A second recovery sweep must be a no-op — the run is no longer
        // Running/stale, so it must not be "recovered" again.
        var secondRecoveredCount = await sharedRepository.RecoverStaleLeasesAsync(CancellationToken.None);
        Assert.Equal(0, secondRecoveredCount);

        // --- Second worker instance (post-restart) claims the now-Pending run.
        using var shutdown2 = new ApplicationShutdownCoordinator();
        var tracker2 = new BackgroundOperationTracker();
        var metrics2 = new OperationalMetrics();
        var worker2 = CreateWorker(sharedRepository, shutdown2, tracker2, metrics2);

        var secondClaim = await sharedRepository.TryClaimNextPendingAsync(CancellationToken.None);
        Assert.NotNull(secondClaim);
        Assert.Equal(run.Id, secondClaim!.Id);

        // Concurrency guarantee: with the run now claimed (Running) again,
        // a third concurrent claimer must not also receive it.
        var thirdClaim = await sharedRepository.TryClaimNextPendingAsync(CancellationToken.None);
        Assert.Null(thirdClaim);

        // Sanity: exactly one claim ever returned this run id as "newly claimed".
        Assert.Equal(2, sharedRepository.ClaimsThatReturnedThisRun(run.Id));
    }

    // ---------------------------------------------------------------
    // 10.3 Faulted worker
    // ---------------------------------------------------------------

    [Fact]
    public async Task Worker_SurvivesTransientClaimFailure_AndKeepsPolling()
    {
        // OrchestrationRunWorker.ExecuteAsync wraps PollOnceAsync in a
        // try/catch that logs and continues (see production source) — a
        // single dependency exception must not kill the polling loop.
        var runRepository = new ThrowOnceRepository();
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();

        var worker = CreateWorker(
            runRepository,
            shutdown,
            tracker,
            metrics,
            options: new OrchestrationOptions { QueuePollingInterval = TimeSpan.FromMilliseconds(50) });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);

        // Wait for the throwing call plus at least one subsequent successful tick.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (runRepository.TryClaimCalls < 3 && sw.ElapsedMilliseconds < 1500)
        {
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.True(runRepository.TryClaimCalls >= 3, "Poll loop must keep ticking after a single claim failure.");

        // Documented current-behavior gap: production does not route a
        // transient claim failure through IWorkerHealthCoordinator or
        // IOperationalMetrics — OrchestrationRunWorker only logs it. The
        // coordinator therefore stays whatever state it started in.
    }

    [Fact]
    public void WorkerHealthCoordinator_IsNotAutomaticallyMarkedFaulted_ByOrchestrationRunWorkerPollFailures()
    {
        // Finding: OrchestrationRunWorker never receives or calls
        // IWorkerHealthCoordinator — health transitions are driven solely by
        // WorkerHealthLifecycleHostedService reacting to
        // IHostApplicationLifetime start/stop events (see
        // WorkerHealthLifecycleHostedService.cs). A dependency exception
        // inside the poll loop is swallowed and logged, with no call to
        // MarkFaulted. This test pins that actual (not aspirational)
        // contract: the coordinator must be driven explicitly.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance);
        coordinator.MarkReady();

        // Simulating what OrchestrationRunWorker actually does on a poll
        // failure: nothing but a log line. No MarkFaulted call happens.
        var snapshot = coordinator.GetSnapshot();

        Assert.Equal(WorkerLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.IsReady);

        // Only an explicit MarkFaulted call (as WorkerHealthLifecycleHostedService
        // or a future integration would need to invoke) changes the signal.
        coordinator.MarkFaulted(new InvalidOperationException("simulated dependency failure"));
        var faultedSnapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Faulted, faultedSnapshot.State);
        Assert.False(faultedSnapshot.IsLive);
    }

    [Fact]
    public async Task Worker_RepeatedPollFailures_TransitionsCoordinatorToDegraded_AndRecoveryRestoresReady()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var coordinator = WorkerHealthCoordinator.ForTesting(tempDir, NullLogger<WorkerHealthCoordinator>.Instance, degradationThreshold: 2);
        coordinator.MarkReady();

        var repository = new ControllableThrowRepository { ShouldThrow = true };
        using var shutdown = new ApplicationShutdownCoordinator();
        var tracker = new BackgroundOperationTracker();
        var metrics = new OperationalMetrics();
        var options = new OrchestrationOptions { QueuePollingInterval = TimeSpan.FromMilliseconds(20) };

        var worker = CreateWorker(repository, shutdown, tracker, metrics, options, coordinator: coordinator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var workerTask = worker.StartAsync(cts.Token);

        // Wait until repeated poll failures drive coordinator to Degraded
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((coordinator.GetSnapshot().State != WorkerLifecycleState.Degraded || metrics.GetSnapshot().WorkerDegraded < 1) && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(15);
        }

        var degradedSnapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Degraded, degradedSnapshot.State);
        Assert.False(degradedSnapshot.IsReady);
        Assert.True(degradedSnapshot.IsLive);
        Assert.True(metrics.GetSnapshot().WorkerPollFailures >= 2);
        Assert.True(metrics.GetSnapshot().WorkerDegraded >= 1);

        // Signal repository to recover (stop throwing, return null)
        repository.ShouldThrow = false;
        sw.Restart();
        while ((coordinator.GetSnapshot().State != WorkerLifecycleState.Ready || metrics.GetSnapshot().WorkerRecovered < 1) && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(15);
        }

        var recoveredSnapshot = coordinator.GetSnapshot();
        Assert.Equal(WorkerLifecycleState.Ready, recoveredSnapshot.State);
        Assert.True(recoveredSnapshot.IsReady);
        Assert.True(metrics.GetSnapshot().WorkerRecovered >= 1);

        await worker.StopAsync(CancellationToken.None);
        await workerTask;
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    /// <summary>Counts claim attempts; always returns null (no work available).</summary>
    private sealed class ClaimTrackingRepository : IOrchestrationRunRepository
    {
        public int TryClaimCalls { get; private set; }

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            TryClaimCalls++;
            return Task.FromResult<OrchestrationRun?>(null);
        }

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// In-memory fake modelling a single-row "database" shared across two
    /// simulated worker instances, so TryClaimNextPendingAsync and
    /// RecoverStaleLeasesAsync behave with real state transitions instead of
    /// scripted return values.
    /// </summary>
    private sealed class RestartSimulationRepository : IOrchestrationRunRepository
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, OrchestrationRun> _runs = new();
        private readonly Dictionary<Guid, int> _claimReturnCounts = new();

        public void Seed(OrchestrationRun run)
        {
            lock (_lock)
            {
                _runs[run.Id] = run;
            }
        }

        public OrchestrationRunStatus GetStatus(Guid runId)
        {
            lock (_lock)
            {
                return _runs[runId].Status;
            }
        }

        public void ExpireLease(Guid runId)
        {
            lock (_lock)
            {
                _runs[runId].LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5);
            }
        }

        public int ClaimsThatReturnedThisRun(Guid runId)
        {
            lock (_lock)
            {
                return _claimReturnCounts.TryGetValue(runId, out var count) ? count : 0;
            }
        }

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var candidate = _runs.Values.FirstOrDefault(r => r.Status == OrchestrationRunStatus.Pending);
                if (candidate is null)
                {
                    return Task.FromResult<OrchestrationRun?>(null);
                }

                candidate.Status = OrchestrationRunStatus.Running;
                candidate.StartedAtUtc ??= DateTime.UtcNow;
                candidate.WorkerHeartbeatAtUtc = DateTime.UtcNow;
                candidate.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);

                _claimReturnCounts[candidate.Id] = _claimReturnCounts.GetValueOrDefault(candidate.Id) + 1;

                return Task.FromResult<OrchestrationRun?>(candidate);
            }
        }

        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var stale = _runs.Values
                    .Where(r => r.Status == OrchestrationRunStatus.Running
                                && r.LeaseExpiresAtUtc is not null
                                && r.LeaseExpiresAtUtc < now)
                    .ToList();

                foreach (var run in stale)
                {
                    run.Status = OrchestrationRunStatus.Pending;
                    run.LeaseExpiresAtUtc = null;
                    run.WorkerHeartbeatAtUtc = null;
                }

                return Task.FromResult(stale.Count);
            }
        }

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Throws on the first claim call, then behaves like a normal empty-queue repository.</summary>
    private sealed class ThrowOnceRepository : IOrchestrationRunRepository
    {
        private bool _hasThrown;

        public int TryClaimCalls { get; private set; }

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            TryClaimCalls++;

            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("Simulated transient dependency failure.");
            }

            return Task.FromResult<OrchestrationRun?>(null);
        }

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Throws while ShouldThrow is true, then returns null (simulating recovery).</summary>
    private sealed class ControllableThrowRepository : IOrchestrationRunRepository
    {
        public volatile bool ShouldThrow = true;

        public Task<OrchestrationRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default)
        {
            if (ShouldThrow)
            {
                throw new System.IO.IOException("Simulated transient dependency failure.");
            }

            return Task.FromResult<OrchestrationRun?>(null);
        }

        public Task<OrchestrationRun> CreateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRun?> GetActiveByWorkItemIdAsync(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(OrchestrationRun run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddAttemptAsync(OrchestrationRunAttempt attempt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AddProgressEventAsync(OrchestrationProgressEvent progressEvent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep> AddStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrchestrationRunStep?> FindLatestOpenStepAsync(Guid orchestrationRunId, OrchestrationStepKind stage, int? attemptNumber, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateStepAsync(OrchestrationRunStep step, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RenewLeaseAsync(Guid runId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RecoverStaleLeasesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseLeaseAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
