using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Worker;

/// <summary>
/// Phase 18.3 §12 — periodic driver for ITaskDispatchScheduler.RunCycleAsync,
/// one sweep across every non-terminal Project per tick. Structurally mirrors
/// AgentRunWorker's poll loop (per-tick failure isolation, shutdown-aware).
/// Does not execute anything itself — a dispatched WorkItem's actual
/// execution is picked up by the existing AgentRunWorker.
///
/// Phase 18.25 — the same sweep also drives IProjectLifecycleOrchestrator for
/// each Project, immediately before its dispatch cycle. Reusing this one loop
/// rather than adding a second poll loop keeps a single periodic sweep over
/// Projects, and means a Project that advances into Implementation on a tick
/// is dispatched on that same tick. All lifecycle state lives in the Project
/// row, so a Worker restart simply resumes the sweep (§8).
/// </summary>
public sealed class TaskSchedulerHostedService : BackgroundService
{
    private static readonly IReadOnlyCollection<ProjectStatus> TerminalProjectStatuses =
        new[] { ProjectStatus.Completed, ProjectStatus.Failed, ProjectStatus.Cancelled };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskSchedulerOptions _options;
    private readonly IApplicationShutdown _shutdown;
    private readonly ILogger<TaskSchedulerHostedService> _logger;

    public TaskSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        TaskSchedulerOptions options,
        IApplicationShutdown shutdown,
        ILogger<TaskSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _shutdown = shutdown;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskSchedulerHostedService started. Enabled={Enabled}", _options.Enabled);

        using var timer = new PeriodicTimer(_options.SchedulingInterval);

        do
        {
            if (_shutdown.IsStopping || stoppingToken.IsCancellationRequested || !_options.Enabled)
            {
                continue;
            }

            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsStopping)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single failed sweep must never take the whole loop
                // down — the next tick tries again.
                _logger.LogError(ex, "TaskSchedulerSweepFailed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("TaskSchedulerHostedService stopped.");
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
        var statusTracker = scope.ServiceProvider.GetRequiredService<ISchedulerStatusTracker>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IProjectLifecycleOrchestrator>();

        var active = (await projects.ListAsync(cancellationToken))
            .Where(x => !TerminalProjectStatuses.Contains(x.Status))
            .ToList();

        foreach (var project in active)
        {
            if (_shutdown.IsStopping || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // Phase 18.25 — a Project that was never explicitly started is
                // a guaranteed no-op here; only the durable
                // LifecycleStartedAtUtc marker makes it lifecycle-owned.
                var lifecycleResult = await lifecycle.AdvanceAsync(project.Id, cancellationToken);

                if (lifecycleResult.Action != ProjectLifecycleAction.None)
                {
                    _logger.LogInformation(
                        "ProjectLifecycleCycle ProjectId={ProjectId} Action={Action} Status={Status} Reason={Reason}",
                        project.Id, lifecycleResult.Action, lifecycleResult.Status, lifecycleResult.Reason);
                }

                var result = await scheduler.RunCycleAsync(project.Id, cancellationToken);
                statusTracker.RecordCycle(result);

                if (result.DispatchedCount > 0 || result.FailureCount > 0)
                {
                    _logger.LogInformation(
                        "SchedulerCycleCompleted ProjectId={ProjectId} Dispatched={Dispatched} Failures={Failures} SkippedForConcurrency={Skipped}",
                        project.Id, result.DispatchedCount, result.FailureCount, result.SkippedDueToConcurrencyLimit);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One project's cycle failing must not stop the sweep of
                // the rest — Phase 18.3 §13. The same isolation covers a
                // lifecycle step: a Project whose lifecycle advance throws is
                // left exactly as it was, for the next tick to retry.
                _logger.LogError(ex, "SchedulerCycleFailed ProjectId={ProjectId}", project.Id);
            }
        }
    }
}
