using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Worker;

/// <summary>
/// Thin execution host for AgentRun: claims one Queued AgentRun at a time
/// via IAgentRunRepository.TryClaimNextPendingAsync and hands it to
/// IAgentOrchestrator. Structurally mirrors OrchestrationRunWorker's
/// claim/execute/shutdown pattern (poll loop, per-poll failure isolation,
/// graceful drain) without its lease-renewal machinery — one execution loop,
/// one AgentRun at a time is the deliberate scope for this phase.
/// </summary>
public sealed class AgentRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentWorkerOptions _options;
    private readonly IApplicationShutdown _shutdown;
    private readonly IBackgroundOperationTracker _tracker;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<AgentRunWorker> _logger;
    private readonly IWorkerHealthCoordinator? _coordinator;

    public AgentRunWorker(
        IServiceScopeFactory scopeFactory,
        AgentWorkerOptions options,
        IApplicationShutdown shutdown,
        IBackgroundOperationTracker tracker,
        IOperationalMetrics metrics,
        ILogger<AgentRunWorker> logger,
        IWorkerHealthCoordinator? coordinator = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _shutdown = shutdown;
        _tracker = tracker;
        _metrics = metrics;
        _logger = logger;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentRunWorker started.");

        using var timer = new PeriodicTimer(_options.PollingInterval);

        do
        {
            if (_shutdown.IsStopping || stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var wasDegraded = _coordinator?.GetSnapshot().State == WorkerLifecycleState.Degraded;
                await PollOnceAsync(stoppingToken);
                _coordinator?.ReportPollSuccess();
                if (wasDegraded && _coordinator?.GetSnapshot().State == WorkerLifecycleState.Ready)
                {
                    _metrics.RecordWorkerRecovered();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single failed AgentRun/poll must never take the whole
                // loop down — the next tick tries again.
                _logger.LogError(ex, "AgentRun poll failed.");
                _metrics.RecordWorkerPollFailure();
                var wasDegraded = _coordinator?.GetSnapshot().State == WorkerLifecycleState.Degraded;
                _coordinator?.ReportPollFailure(ex);
                if (!wasDegraded && _coordinator?.GetSnapshot().State == WorkerLifecycleState.Degraded)
                {
                    _metrics.RecordWorkerDegraded();
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("AgentRunWorker stopped polling.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AgentRunWorker stopping. Active operations: {ActiveCount}.", _tracker.ActiveCount);
        _metrics.RecordWorkerStopping();

        _shutdown.TryBeginShutdown();

        var drained = await _tracker.WaitForDrainAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (!drained)
        {
            _logger.LogWarning(
                "AgentRunWorker shutdown timed out with {RemainingCount} operation(s) still active.", _tracker.ActiveCount);
            _metrics.RecordShutdownTimedOut();
        }

        _metrics.RecordWorkerStopped();
        _logger.LogInformation("AgentRunWorker stopped.");

        await base.StopAsync(cancellationToken);
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        if (_shutdown.IsStopping || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();

        var run = await runRepository.TryClaimNextPendingAsync(stoppingToken);

        if (run is null)
        {
            _logger.LogDebug("AgentRunWorker idle — no queued AgentRun.");
            return;
        }

        _metrics.RecordAgentWorkerClaimSuccess();
        _logger.LogInformation("AgentRun {AgentRunId} claimed by worker.", run.Id);

        var operationContext = new OperationContext(
            CorrelationId: run.CorrelationId,
            OperationId: run.Id,
            ParentOperationId: null,
            OperationName: "AgentRun",
            ProjectId: run.ProjectId,
            TaskId: run.WorkItemId,
            RunId: run.Id,
            StartedAtUtc: DateTime.UtcNow);

        using var trackHandle = _tracker.Track(operationContext);

        using var executionScope = _scopeFactory.CreateScope();
        var orchestrator = executionScope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.StoppingToken);
        executionCts.CancelAfter(_options.ExecutionTimeout);

        // Phase 18.4: heartbeat the linked WorkItem (if any) for the
        // duration of orchestrator execution, so the existing
        // WorkflowRecoveryHostedService/ITaskStateManager stale-execution
        // sweep can tell a genuinely still-running AgentRun-backed WorkItem
        // apart from an orphaned one. Independent linked token — cancelling
        // it does not touch executionCts/the orchestrator call, only stops
        // this loop.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(executionCts.Token);
        var heartbeatTask = RunHeartbeatLoopAsync(
            run.Id,
            run.WorkItemId,
            heartbeatCts.Token);

        try
        {
            var result = await orchestrator.ExecuteAsync(run.Id, executionCts.Token);

            // Stop the heartbeat before the WorkItem sync below runs, so no
            // straggling heartbeat write can race the terminal WorkItem
            // transition (Phase 18.4 §7/§11).
            await StopHeartbeatAsync(heartbeatCts, heartbeatTask);

            _metrics.RecordAgentExecution(result.Success, result.DurationMs);
            RecordLifecycleMetrics(result);

            if (result.Executed && run.WorkItemId is not null)
            {
                // Phase 18.3 §10: AgentOrchestrator already persisted the
                // terminal AgentRun (including ErrorMessage) — reload it
                // rather than reconstructing from AgentOrchestrationResult.
                var runRepositoryForSync = executionScope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
                var completedRun = await runRepositoryForSync.GetByIdAsync(run.Id, CancellationToken.None);

                if (completedRun is not null)
                {
                    var sync = executionScope.ServiceProvider.GetRequiredService<IAgentRunWorkItemSync>();
                    await sync.SyncAsync(completedRun, CancellationToken.None);
                }
            }
            else if (result.Executed && run.WorkItemId is null && run.ProjectId is not null)
            {
                // Phase 18.6 — a Planner AgentRun has no WorkItemId (it is a
                // Project-level run, not a WorkItem execution), so the branch
                // above never fires for it. IProjectPlanner.MaterializeAsync
                // itself re-checks AgentType/terminal status and is a no-op
                // for any other Project-level, WorkItem-less AgentRun.
                var planner = executionScope.ServiceProvider.GetRequiredService<IProjectPlanner>();
                await planner.MaterializeAsync(run.Id, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
        {
            _logger.LogInformation(
                "AgentRun {AgentRunId} interrupted by shutdown — left in its current state for the next investigation/attempt.",
                run.Id);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && !_shutdown.IsStopping)
        {
            // The normal timeout path never reaches here — the execution adapter
            // already converts a cancelled execution into a classified,
            // non-thrown failure result, which the orchestrator persists as
            // TimedOut/Cancelled. This branch only guards a cancellation
            // surfacing directly from a repository call around that window.
            _logger.LogWarning("AgentRun {AgentRunId} exceeded the configured execution timeout.", run.Id);
        }
        finally
        {
            // Safety net for the exception paths above (StopHeartbeatAsync
            // was already called on the normal-completion path — calling it
            // again here is a harmless no-op against an already-completed task).
            await StopHeartbeatAsync(heartbeatCts, heartbeatTask);
        }
    }

    /// <summary>
    /// Cancels and awaits the heartbeat loop to completion. Idempotent —
    /// safe to call twice (Cancel() on an already-cancelled source is a
    /// no-op; awaiting an already-completed task returns immediately).
    /// </summary>
    private static async Task StopHeartbeatAsync(CancellationTokenSource heartbeatCts, Task? heartbeatTask)
    {
        if (heartbeatTask is null)
        {
            return;
        }

        heartbeatCts.Cancel();

        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Periodically refreshes WorkItem.LastHeartbeatAtUtc and AgentRun.UpdatedAtUtc
    /// (purely observational — never touches Status/AttemptCount, so it cannot resurrect
    /// a terminal WorkItem or bypass WorkItemTransitions) for as long as
    /// cancellationToken allows. A transient heartbeat failure (e.g. a lost
    /// race with a concurrent recovery sweep) is logged and counted but
    /// never terminates the loop — the AgentRun execution itself remains
    /// authoritative (Phase 18.4 §10).
    ///
    /// Phase 18.29.1 — this loop owns its OWN DI scope, and therefore its own
    /// FactoryDbContext. It previously resolved ITaskStateManager and
    /// IAgentRunRepository from the caller's executionScope, which meant the
    /// heartbeat and the orchestrator were issuing concurrent operations against a
    /// single scoped DbContext — EF Core answers that with "A second operation was
    /// started on this context instance before a previous operation completed".
    /// It only surfaced once a real containerized Implementation run started writing
    /// workspace artifacts (FileSystemArtifactRecorder) while a heartbeat tick was
    /// in flight, which is exactly the overlap this loop exists to create.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(
        Guid agentRunId,
        Guid? workItemId,
        CancellationToken cancellationToken)
    {
        using var heartbeatScope = _scopeFactory.CreateScope();
        var taskStateManager = heartbeatScope.ServiceProvider.GetRequiredService<ITaskStateManager>();
        var agentRunRepository = heartbeatScope.ServiceProvider.GetRequiredService<IAgentRunRepository>();

        // Immediate initial heartbeat upon starting execution
        try
        {
            await agentRunRepository.HeartbeatAsync(agentRunId, cancellationToken);
            if (workItemId is { } wid)
            {
                await taskStateManager.HeartbeatAsync(wid, "AgentExecutionInProgress", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial AgentRunHeartbeatFailed AgentRunId={AgentRunId} WorkItemId={WorkItemId}", agentRunId, workItemId);
            _metrics.RecordAgentRunHeartbeatFailure();
        }

        try
        {
            while (true)
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);

                try
                {
                    await agentRunRepository.HeartbeatAsync(agentRunId, cancellationToken);
                    if (workItemId is { } wid)
                    {
                        await taskStateManager.HeartbeatAsync(wid, "AgentExecutionInProgress", cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AgentRunHeartbeatFailed AgentRunId={AgentRunId} WorkItemId={WorkItemId}", agentRunId, workItemId);
                    _metrics.RecordAgentRunHeartbeatFailure();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop signal — execution finished, or Worker shutting down.
        }
    }

    /// <summary>
    /// Fires once execution is confirmed to have happened (Executed: true),
    /// keyed off the orchestrator's own FinalStatus — the one place both the
    /// Worker-claimed path and any future direct-invocation path funnel
    /// through, so metrics stay accurate regardless of caller.
    /// </summary>
    private void RecordLifecycleMetrics(AgentOrchestrationResult result)
    {
        if (!result.Executed)
        {
            return;
        }

        _metrics.RecordAgentRunStarted();

        switch (result.FinalStatus)
        {
            case AgentRunStatus.Succeeded:
                _metrics.RecordAgentRunSucceeded();
                break;
            case AgentRunStatus.Failed:
                _metrics.RecordAgentRunFailed();
                break;
            case AgentRunStatus.TimedOut:
                _metrics.RecordAgentRunTimedOut();
                break;
            case AgentRunStatus.Cancelled:
                _metrics.RecordAgentRunCancelled();
                break;
        }
    }
}
