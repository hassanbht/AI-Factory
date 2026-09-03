using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Shutdown;

/// <summary>
/// Singleton coordinator for application shutdown. Thread-safe, observable, and integrates
/// with IHostApplicationLifetime.ApplicationStopping while remaining fully testable without a host.
/// </summary>
public sealed class ApplicationShutdownCoordinator : IApplicationShutdown, IDisposable
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILogger<ApplicationShutdownCoordinator>? _logger;
    private readonly CancellationTokenRegistration? _stoppingRegistration;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _shutdownInitiated;
    private volatile bool _isStopping;
    private bool _disposed;

    public ApplicationShutdownCoordinator(
        IHostApplicationLifetime? hostLifetime = null,
        IServiceScopeFactory? scopeFactory = null,
        IOperationalMetrics? metrics = null,
        ILogger<ApplicationShutdownCoordinator>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;

        if (hostLifetime is not null)
        {
            _stoppingRegistration = hostLifetime.ApplicationStopping.Register(() =>
            {
                _logger?.LogInformation("IHostApplicationLifetime.ApplicationStopping triggered application shutdown.");
                TryBeginShutdown();
            });
        }
    }

    public bool IsStopping => _isStopping;

    public CancellationToken StoppingToken => _cts.Token;

    public bool TryBeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownInitiated, 1) != 0)
        {
            return false;
        }

        _isStopping = true;
        _logger?.LogInformation("Application shutdown initiated.");
        _metrics?.RecordShutdownInitiated();

        if (_scopeFactory is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();
                    if (auditLogger is not null)
                    {
                        await auditLogger.LogAsync(
                            Guid.NewGuid(),
                            AuditEventType.ShutdownInitiated,
                            AuditSeverity.Info,
                            "Application shutdown initiated.",
                            cancellationToken: CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to write ShutdownInitiated audit event.");
                }
            });
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _shutdownTcs.TrySetResult();
        return true;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_isStopping)
        {
            return Task.CompletedTask;
        }

        return _shutdownTcs.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stoppingRegistration?.Dispose();
        _cts.Dispose();
    }
}
