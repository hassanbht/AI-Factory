using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly string _healthDir;
    private readonly string _readyFilePath;
    private readonly string _liveFilePath;
    private readonly string _degradedFilePath;
    private readonly WorkerHealthOptions _options;

    private WorkerLifecycleState _state = WorkerLifecycleState.Starting;
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private string? _lastError;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private DateTime? _lastFailureUtc;

    public WorkerHealthCoordinator(
        WorkerHealthOptions options,
        string? healthDirectory = null,
        ILogger<WorkerHealthCoordinator>? logger = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<WorkerHealthCoordinator>.Instance;
        _scopeFactory = scopeFactory;

        _healthDir = healthDirectory
                     ?? Environment.GetEnvironmentVariable("AI_FACTORY_HEALTH_DIR")
                     ?? Path.Combine(Path.GetTempPath(), "ai-factory-health");

        _readyFilePath = Path.Combine(_healthDir, "worker.ready");
        _liveFilePath = Path.Combine(_healthDir, "worker.live");
        _degradedFilePath = Path.Combine(_healthDir, "worker.degraded");

        EnsureDirectory();
        TouchLivenessFile();
    }

    /// <summary>Test entry point — a bare failure threshold with recovery-on-first-success and no failure-window reset.</summary>
    public static WorkerHealthCoordinator ForTesting(
        string? healthDirectory = null,
        ILogger<WorkerHealthCoordinator>? logger = null,
        int degradationThreshold = 3) =>
        new(
            new WorkerHealthOptions
            {
                ConsecutiveFailureThreshold = degradationThreshold > 0 ? degradationThreshold : 3,
                RecoverySuccessThreshold = 1,
                FailureWindow = TimeSpan.MaxValue,
            },
            healthDirectory,
            logger);

    public WorkerHealthSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new WorkerHealthSnapshot(
                State: _state,
                IsReady: _state == WorkerLifecycleState.Ready,
                IsLive: _state != WorkerLifecycleState.Faulted,
                StartedAtUtc: _startedAtUtc,
                LastError: _lastError,
                ConsecutiveFailures: _consecutiveFailures);
        }
    }

    public void MarkReady()
    {
        lock (_lock)
        {
            _state = WorkerLifecycleState.Ready;
            _consecutiveFailures = 0;
            _lastError = null;
            RemoveFile(_degradedFilePath);
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
            RemoveFile(_degradedFilePath);
            _logger.LogInformation("Worker marked as Stopping (readiness revoked).");
        }
    }

    public void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            _state = WorkerLifecycleState.Faulted;
            _lastError = AI.Factory.Application.Security.SensitiveDataSanitizer.Sanitize(ex.Message);
            RemoveFile(_readyFilePath);
            RemoveFile(_degradedFilePath);
            RemoveFile(_liveFilePath);
            _logger.LogError(ex, "Worker marked as Faulted: State={State}, Message={Message}", _state, _lastError);
            RaiseAuditEvent(AuditEventType.WorkerFaulted, AuditSeverity.Critical, $"Worker faulted: {_lastError}");
        }
    }

    public void ReportPollFailure(Exception ex)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_lastFailureUtc.HasValue && now - _lastFailureUtc.Value > _options.FailureWindow)
            {
                // Prior failures are outside the failure window — this is a new incident, not a continuation.
                _consecutiveFailures = 0;
            }

            _consecutiveFailures++;
            _consecutiveSuccesses = 0;
            _lastFailureUtc = now;
            _lastError = AI.Factory.Application.Security.SensitiveDataSanitizer.Sanitize(ex.Message);
            var classification = ex.GetType().Name;

            if (_state is WorkerLifecycleState.Ready or WorkerLifecycleState.Degraded)
            {
                if (_consecutiveFailures >= _options.ConsecutiveFailureThreshold && _state != WorkerLifecycleState.Degraded)
                {
                    _state = WorkerLifecycleState.Degraded;
                    RemoveFile(_readyFilePath);
                    TouchFile(_degradedFilePath);
                    TouchFile(_liveFilePath);
                    _logger.LogWarning(
                        "Worker marked as Degraded: FailureClassification={Classification}, ConsecutiveFailures={Failures}, Threshold={Threshold}, Message={Message}",
                        classification, _consecutiveFailures, _options.ConsecutiveFailureThreshold, _lastError);
                    RaiseAuditEvent(AuditEventType.WorkerDegraded, AuditSeverity.Warning,
                        $"Worker degraded after {_consecutiveFailures} consecutive poll failure(s) ({classification}): {_lastError}");
                }
                else
                {
                    _logger.LogInformation(
                        "Worker recorded transient poll failure: State={State}, FailureClassification={Classification}, ConsecutiveFailures={Failures}/{Threshold}, Message={Message}",
                        _state, classification, _consecutiveFailures, _options.ConsecutiveFailureThreshold, _lastError);
                }
            }
        }
    }

    public void ReportPollSuccess()
    {
        lock (_lock)
        {
            if (_state == WorkerLifecycleState.Degraded)
            {
                _consecutiveSuccesses++;
                if (_consecutiveSuccesses < _options.RecoverySuccessThreshold)
                {
                    _logger.LogInformation(
                        "Worker recovering: ConsecutiveSuccesses={Successes}/{Threshold} while Degraded.",
                        _consecutiveSuccesses, _options.RecoverySuccessThreshold);
                    return;
                }

                _logger.LogInformation(
                    "Worker recovered after {Successes} consecutive successful poll(s). Restoring Ready state.",
                    _consecutiveSuccesses);
                _consecutiveFailures = 0;
                _consecutiveSuccesses = 0;
                _lastFailureUtc = null;
                _lastError = null;
                RemoveFile(_degradedFilePath);
                _state = WorkerLifecycleState.Ready;
                TouchFile(_readyFilePath);
                TouchFile(_liveFilePath);
                RaiseAuditEvent(AuditEventType.WorkerRecovered, AuditSeverity.Info, "Worker recovered from Degraded to Ready.");
                return;
            }

            if (_consecutiveFailures > 0)
            {
                _consecutiveFailures = 0;
                _consecutiveSuccesses = 0;
                _lastFailureUtc = null;
            }
        }
    }

    /// <summary>
    /// Fire-and-forget audit write, mirroring CircuitBreakerRegistry's pattern for a
    /// singleton service emitting audit events through a scoped IAuditLogger — a single
    /// successful poll must not itself become an audit event (would be noise), only the
    /// state transitions raised by callers above.
    /// </summary>
    private void RaiseAuditEvent(AuditEventType eventType, AuditSeverity severity, string message)
    {
        if (_scopeFactory is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();
                if (auditLogger is not null)
                {
                    await auditLogger.LogAsync(
                        executionId: Guid.NewGuid(),
                        eventType: eventType,
                        severity: severity,
                        message: message,
                        outcome: eventType.ToString(),
                        actorType: "System");
                }
            }
            catch
            {
                // Audit persistence failures must never affect worker health tracking.
            }
        });
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
