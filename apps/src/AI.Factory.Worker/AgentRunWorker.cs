using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
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

        try
        {
            var result = await orchestrator.ExecuteAsync(run.Id, executionCts.Token);
            _metrics.RecordAgentExecution(result.Success, result.DurationMs);
            RecordLifecycleMetrics(result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
        {
            _logger.LogInformation(
                "AgentRun {AgentRunId} interrupted by shutdown — left in its current state for the next investigation/attempt.",
                run.Id);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && !_shutdown.IsStopping)
        {
            // The normal timeout path never reaches here — HermesAgentRuntime
            // already converts a cancelled execution into a classified,
            // non-thrown failure result, which the orchestrator persists as
            // TimedOut/Cancelled. This branch only guards a cancellation
            // surfacing directly from a repository call around that window.
            _logger.LogWarning("AgentRun {AgentRunId} exceeded the configured execution timeout.", run.Id);
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
