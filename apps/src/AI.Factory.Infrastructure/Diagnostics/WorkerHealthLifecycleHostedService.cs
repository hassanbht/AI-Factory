using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Infrastructure.Diagnostics;

/// <summary>
/// Connects Worker lifecycle events (start, shutdown) with the IWorkerHealthCoordinator.
/// Marks worker as Ready once host has completely started, and revokes readiness when stopping.
/// </summary>
public sealed class WorkerHealthLifecycleHostedService : IHostedService
{
    private readonly IWorkerHealthCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WorkerHealthLifecycleHostedService> _logger;

    public WorkerHealthLifecycleHostedService(
        IWorkerHealthCoordinator coordinator,
        IHostApplicationLifetime lifetime,
        ILogger<WorkerHealthLifecycleHostedService>? logger = null)
    {
        _coordinator = coordinator;
        _lifetime = lifetime;
        _logger = logger ?? NullLogger<WorkerHealthLifecycleHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() =>
        {
            _coordinator.MarkReady();
            _logger.LogInformation("Worker health lifecycle: Host started -> Worker Ready.");
        });

        _lifetime.ApplicationStopping.Register(() =>
        {
            _coordinator.MarkStopping();
            _logger.LogInformation("Worker health lifecycle: Host stopping -> Worker Stopping (readiness revoked).");
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _coordinator.MarkStopping();
        return Task.CompletedTask;
    }
}
