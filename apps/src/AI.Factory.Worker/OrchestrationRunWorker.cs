using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Worker;

/// <summary>
/// The Worker's core loop: retrieve pending runs, claim one, execute it,
/// persist progress, handle failure safely — the API only ever creates a
/// Pending row and returns; all real execution happens here, off the HTTP
/// request. Coordinates safe shutdown, in-flight tracking, and cancellation propagation.
/// </summary>
public sealed class OrchestrationRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrchestrationOptions _options;
    private readonly ShutdownOptions _shutdownOptions;
    private readonly IApplicationShutdown _shutdown;
    private readonly IBackgroundOperationTracker _tracker;
    private readonly IAuditLogger _auditLogger;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<OrchestrationRunWorker> _logger;
    private readonly IWorkerHealthCoordinator? _coordinator;

    public OrchestrationRunWorker(
        IServiceScopeFactory scopeFactory,
        OrchestrationOptions options,
        ShutdownOptions shutdownOptions,
        IApplicationShutdown shutdown,
        IBackgroundOperationTracker tracker,
        IAuditLogger auditLogger,
        IOperationalMetrics metrics,
        ILogger<OrchestrationRunWorker> logger,
        IWorkerHealthCoordinator? coordinator = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _shutdownOptions = shutdownOptions;
        _shutdown = shutdown;
        _tracker = tracker;
        _auditLogger = auditLogger;
        _metrics = metrics;
        _logger = logger;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.QueuePollingInterval);

        do
        {
            if (_shutdown.IsStopping || stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await PollOnceAsync(stoppingToken);
                if (_coordinator?.GetSnapshot().State == WorkerLifecycleState.Degraded)
                {
                    _metrics.RecordWorkerRecovered();
                }
                _coordinator?.ReportPollSuccess();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single poll failing must never take the whole loop down
                // — the next tick tries again.
                _logger.LogError(ex, "Orchestration run poll failed.");
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
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("OrchestrationRunWorker stopping initiated. Active operations: {ActiveCount}.", _tracker.ActiveCount);
        _metrics.RecordWorkerStopping();

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkerStopping,
            AuditSeverity.Info,
            $"Worker stopping initiated with {_tracker.ActiveCount} active operation(s).",
            cancellationToken: CancellationToken.None);

        _shutdown.TryBeginShutdown();

        var gracefulTimeout = TimeSpan.FromSeconds(_shutdownOptions.GracefulTimeoutSeconds);
        var drained = await _tracker.WaitForDrainAsync(gracefulTimeout, cancellationToken);

        if (drained)
        {
            _logger.LogInformation("All in-flight operations drained successfully in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            _metrics.RecordOperationsDrained(1);
            await _auditLogger.LogAsync(
                Guid.NewGuid(),
                AuditEventType.OperationsDrained,
                AuditSeverity.Info,
                $"In-flight operations drained successfully in {sw.ElapsedMilliseconds}ms.",
                cancellationToken: CancellationToken.None);
        }
        else
        {
            _logger.LogWarning("Worker shutdown timed out after {ElapsedMs}ms. Remaining active operations: {RemainingCount}.", sw.ElapsedMilliseconds, _tracker.ActiveCount);
            _metrics.RecordShutdownTimedOut();
            _metrics.RecordOperationsCancelled(_tracker.ActiveCount);
            await _auditLogger.LogAsync(
                Guid.NewGuid(),
                AuditEventType.ShutdownTimedOut,
                AuditSeverity.Warning,
                $"Worker shutdown timed out with {_tracker.ActiveCount} operation(s) remaining.",
                cancellationToken: CancellationToken.None);
        }

        _metrics.RecordWorkerStopped();
        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.WorkerStopped,
            AuditSeverity.Info,
            "Worker stopped.",
            cancellationToken: CancellationToken.None);

        await base.StopAsync(cancellationToken);
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        if (_shutdown.IsStopping || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();

        var run = await runRepository.TryClaimNextPendingAsync(stoppingToken);

        if (run is null)
        {
            return;
        }

        var runContext = new OperationContext(
            CorrelationId: run.Id,
            OperationId: run.Id,
            ParentOperationId: null,
            OperationName: "OrchestrationRun",
            ProjectId: run.ProjectId,
            TaskId: run.WorkItemId,
            RunId: run.Id,
            StartedAtUtc: DateTime.UtcNow);

        using var trackHandle = _tracker.Track(runContext);

        using var executionScope = _scopeFactory.CreateScope();
        var engine = executionScope.ServiceProvider.GetRequiredService<OrchestrationRunExecutionEngine>();
        var executionRunRepository = executionScope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.StoppingToken);
        executionCts.CancelAfter(_options.ExecutionTimeout);

        using var leaseRenewalTimer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        var leaseRenewalTask = RenewLeaseUntilDoneAsync(executionRunRepository, run.Id, leaseRenewalTimer, executionCts.Token);

        try
        {
            _logger.LogInformation("OrchestrationRun {RunId} claimed — starting execution.", run.Id);
            await engine.ExecuteAsync(run, executionCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
        {
            _logger.LogInformation("OrchestrationRun {RunId} interrupted by shutdown. Releasing lease back to Pending.", run.Id);
            try
            {
                await executionRunRepository.ReleaseLeaseAsync(run.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release lease for OrchestrationRun {RunId} on shutdown.", run.Id);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && !_shutdown.IsStopping)
        {
            // Our own ExecutionTimeout fired, not a real shutdown — surface
            // it as a controlled failure rather than an unhandled exception.
            _logger.LogWarning("OrchestrationRun {RunId} exceeded the configured execution timeout.", run.Id);
        }
        finally
        {
            leaseRenewalTimer.Dispose();
            await leaseRenewalTask;
        }
    }

    private static async Task RenewLeaseUntilDoneAsync(
        IOrchestrationRunRepository runRepository, Guid runId, PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(CancellationToken.None))
            {
                await runRepository.RenewLeaseAsync(runId, TimeSpan.FromMinutes(5), CancellationToken.None);
            }
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed by the caller once execution finished — expected shutdown of this loop.
        }
    }
}
