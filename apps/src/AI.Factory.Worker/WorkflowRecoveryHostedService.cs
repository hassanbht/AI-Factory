using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Worker;

/// <summary>
/// Runs once at Worker startup (full-process-restart sweep — everything
/// left Running is genuinely orphaned), then periodically thereafter
/// (staleness sweep — only a Running item whose heartbeat has gone quiet
/// for longer than the configured timeout, safe to run repeatedly since an
/// already-recovered item no longer matches Status==Running).
/// </summary>
public sealed class WorkflowRecoveryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecoveryOptions _recoveryOptions;
    private readonly IApplicationShutdown? _shutdown;
    private readonly ILogger<WorkflowRecoveryHostedService> _logger;

    public WorkflowRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        RecoveryOptions recoveryOptions,
        ILogger<WorkflowRecoveryHostedService> logger,
        IApplicationShutdown? shutdown = null)
    {
        _scopeFactory = scopeFactory;
        _recoveryOptions = recoveryOptions;
        _logger = logger;
        _shutdown = shutdown;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_shutdown?.IsStopping == true || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await RunStartupRecoveryAsync(stoppingToken);

        using var timer = new PeriodicTimer(_recoveryOptions.PeriodicSweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_shutdown?.IsStopping == true || stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await RunStaleSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Phase 18.4 §7: a concurrent AgentRun heartbeat write can
                    // legitimately collide with this sweep's optimistic-
                    // concurrency save (ConcurrencyConflictException) — a
                    // single failed sweep must never take the whole recovery
                    // loop down; the next periodic tick retries.
                    _logger.LogError(ex, "Workflow recovery: stale sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskStateManager = scope.ServiceProvider.GetRequiredService<ITaskStateManager>();

        _logger.LogInformation("Workflow recovery: sweeping stale/orphaned Running executions at startup.");

        // Multi-worker safe: do not kill healthy peer workers that are actively heartbeating.
        // Only recover items whose heartbeat/progress is older than StaleExecutionTimeout.
        var recoveredTasks = await taskStateManager.RecoverStaleExecutionsAsync(cancellationToken);
        if (recoveredTasks > 0)
        {
            _logger.LogWarning("Workflow recovery: recovered {Count} stale WorkItem(s) at startup.", recoveredTasks);
        }

        var agentRuns = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
        var cutoffUtc = DateTime.UtcNow - _recoveryOptions.StaleExecutionTimeout;
        var recoveredRuns = await agentRuns.RecoverOrphanedRunsAsync(cutoffUtc, cancellationToken);

        if (recoveredRuns > 0)
        {
            _logger.LogWarning(
                "AgentRunOrphanRecovery Mode=Startup RecoveredCount={Count}", recoveredRuns);
        }
    }

    private async Task RunStaleSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskStateManager = scope.ServiceProvider.GetRequiredService<ITaskStateManager>();

        var recoveredCount = await taskStateManager.RecoverStaleExecutionsAsync(cancellationToken);

        if (recoveredCount > 0)
        {
            _logger.LogWarning("Workflow recovery: recovered {Count} stale WorkItem(s).", recoveredCount);
        }

        // Periodic mode is cutoff-bounded, so a genuinely executing AgentRun
        // in this same live process is never touched.
        var agentRuns = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
        var recoveredRuns = await agentRuns.RecoverOrphanedRunsAsync(
            DateTime.UtcNow - _recoveryOptions.StaleExecutionTimeout, cancellationToken);

        if (recoveredRuns > 0)
        {
            _logger.LogWarning("AgentRunOrphanRecovery Mode=Stale RecoveredCount={Count}", recoveredRuns);
        }

        var runRepository = scope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();
        var recoveredRunCount = await runRepository.RecoverStaleLeasesAsync(cancellationToken);

        if (recoveredRunCount > 0)
        {
            _logger.LogWarning("Workflow recovery: recovered {Count} stale OrchestrationRun(s) with an expired Worker lease.", recoveredRunCount);
        }
    }
}
