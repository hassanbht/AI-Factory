using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Worker;

/// <summary>
/// The Worker's core loop: retrieve pending runs, claim one, execute it,
/// persist progress, handle failure safely — the API only ever creates a
/// Pending row and returns; all real execution happens here, off the HTTP
/// request (see Program.cs "API should... queue the run" / "Worker should...
/// execute the orchestration").
/// </summary>
public sealed class OrchestrationRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<OrchestrationRunWorker> _logger;

    public OrchestrationRunWorker(
        IServiceScopeFactory scopeFactory, OrchestrationOptions options, ILogger<OrchestrationRunWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.QueuePollingInterval);

        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single poll failing must never take the whole loop down
                // — the next tick tries again.
                _logger.LogError(ex, "Orchestration run poll failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();

        var run = await runRepository.TryClaimNextPendingAsync(stoppingToken);

        if (run is null)
        {
            return;
        }

        using var executionScope = _scopeFactory.CreateScope();
        var engine = executionScope.ServiceProvider.GetRequiredService<OrchestrationRunExecutionEngine>();
        var executionRunRepository = executionScope.ServiceProvider.GetRequiredService<IOrchestrationRunRepository>();

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        executionCts.CancelAfter(_options.ExecutionTimeout);

        using var leaseRenewalTimer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        var leaseRenewalTask = RenewLeaseUntilDoneAsync(executionRunRepository, run.Id, leaseRenewalTimer, executionCts.Token);

        try
        {
            _logger.LogInformation("OrchestrationRun {RunId} claimed — starting execution.", run.Id);
            await engine.ExecuteAsync(run, executionCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
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
