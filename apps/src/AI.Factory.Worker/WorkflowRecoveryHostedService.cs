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
    private readonly ILogger<WorkflowRecoveryHostedService> _logger;

    public WorkflowRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        RecoveryOptions recoveryOptions,
        ILogger<WorkflowRecoveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _recoveryOptions = recoveryOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        using var timer = new PeriodicTimer(_recoveryOptions.PeriodicSweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunStaleSweepAsync(stoppingToken);
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

        _logger.LogInformation("Workflow recovery: sweeping orphaned Running WorkItems at startup.");

        await taskStateManager.RecoverOrphanedTasksAsync(cancellationToken);
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

        var runRepository = scope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();
        var recoveredRunCount = await runRepository.RecoverStaleLeasesAsync(cancellationToken);

        if (recoveredRunCount > 0)
        {
            _logger.LogWarning("Workflow recovery: recovered {Count} stale OrchestrationRun(s) with an expired Worker lease.", recoveredRunCount);
        }
    }
}
