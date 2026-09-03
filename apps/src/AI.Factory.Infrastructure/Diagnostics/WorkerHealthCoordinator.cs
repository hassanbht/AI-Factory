using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Infrastructure.Diagnostics;

/// <summary>
/// Thread-safe worker health and lifecycle coordinator.
/// Manages in-memory health snapshots and optional container probe files.
/// </summary>
public sealed class WorkerHealthCoordinator : IWorkerHealthCoordinator
{
    private readonly object _lock = new();
    private readonly ILogger<WorkerHealthCoordinator> _logger;
    private readonly string _healthDir;
    private readonly string _readyFilePath;
    private readonly string _liveFilePath;

    private WorkerLifecycleState _state = WorkerLifecycleState.Starting;
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private string? _lastError;

    public WorkerHealthCoordinator(
        string? healthDirectory = null,
        ILogger<WorkerHealthCoordinator>? logger = null)
    {
        _logger = logger ?? NullLogger<WorkerHealthCoordinator>.Instance;

        _healthDir = healthDirectory
                     ?? Environment.GetEnvironmentVariable("AI_FACTORY_HEALTH_DIR")
                     ?? Path.Combine(Path.GetTempPath(), "ai-factory-health");

        _readyFilePath = Path.Combine(_healthDir, "worker.ready");
        _liveFilePath = Path.Combine(_healthDir, "worker.live");

        EnsureDirectory();
        TouchLivenessFile();
    }

    public WorkerHealthSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new WorkerHealthSnapshot(
                State: _state,
                IsReady: _state == WorkerLifecycleState.Ready,
                IsLive: _state != WorkerLifecycleState.Faulted,
                StartedAtUtc: _startedAtUtc,
                LastError: _lastError);
        }
    }

    public void MarkReady()
    {
        lock (_lock)
        {
            _state = WorkerLifecycleState.Ready;
            TouchFile(_readyFilePath);
            TouchFile(_liveFilePath);
            _logger.LogInformation("Worker marked as Ready.");
        }
    }

    public void MarkStopping()
    {
        lock (_lock)
        {
            _state = WorkerLifecycleState.Stopping;
            RemoveFile(_readyFilePath);
            _logger.LogInformation("Worker marked as Stopping (readiness revoked).");
        }
    }

    public void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            _state = WorkerLifecycleState.Faulted;
            _lastError = ex.Message;
            RemoveFile(_readyFilePath);
            RemoveFile(_liveFilePath);
            _logger.LogError(ex, "Worker marked as Faulted: {Message}", ex.Message);
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(_healthDir))
            {
                Directory.CreateDirectory(_healthDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure health directory {HealthDir}", _healthDir);
        }
    }

    private void TouchLivenessFile()
    {
        TouchFile(_liveFilePath);
    }

    private void TouchFile(string path)
    {
        try
        {
            EnsureDirectory();
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not touch health file {Path}", path);
        }
    }

    private void RemoveFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete health file {Path}", path);
        }
    }
}
