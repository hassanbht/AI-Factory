using System.Collections.Concurrent;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Application.Resilience;

public sealed class CircuitBreakerRegistry : ICircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, ICircuitBreaker> _circuits = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider _timeProvider;

    public CircuitBreakerRegistry(
        IServiceScopeFactory? scopeFactory = null,
        IOperationalMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ICircuitBreaker GetOrCreate(string name, CircuitBreakerProfileOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _circuits.GetOrAdd(name, n =>
        {
            var opts = options ?? new CircuitBreakerProfileOptions();
            var logger = _loggerFactory?.CreateLogger<CircuitBreakerImpl>() ?? NullLogger<CircuitBreakerImpl>.Instance;
            return new CircuitBreakerImpl(n, opts, _scopeFactory, _metrics, logger, _timeProvider);
        });
    }

    public ICircuitBreaker? TryGet(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        _circuits.TryGetValue(name, out var circuit);
        return circuit;
    }

    public IReadOnlyCollection<CircuitBreakerSnapshot> GetAllSnapshots()
    {
        return _circuits.Values.Select(c => c.GetSnapshot()).ToList();
    }

    internal sealed class CircuitBreakerImpl : ICircuitBreaker
    {
        private readonly CircuitBreakerProfileOptions _options;
        private readonly IServiceScopeFactory? _scopeFactory;
        private readonly IOperationalMetrics? _metrics;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly object _lock = new();

        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private int _halfOpenTrialsIssued;
        private DateTime? _openedAtUtc;
        private long _totalSuccesses;
        private long _totalFailures;

        public string Name { get; }

        public CircuitBreakerImpl(
            string name,
            CircuitBreakerProfileOptions options,
            IServiceScopeFactory? scopeFactory,
            IOperationalMetrics? metrics,
            ILogger logger,
            TimeProvider? timeProvider = null)
        {
            Name = name;
            _options = options;
            _scopeFactory = scopeFactory;
            _metrics = metrics;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public CircuitState State
        {
            get
            {
                lock (_lock)
                {
                    CheckHalfOpenTransitionUnderLock();
                    return _state;
                }
            }
        }

        public bool CanExecuteFast()
        {
            if (!_options.Enabled)
            {
                return true;
            }

            lock (_lock)
            {
                CheckHalfOpenTransitionUnderLock();

                return _state switch
                {
                    CircuitState.Closed => true,
                    CircuitState.HalfOpen => _halfOpenTrialsIssued < _options.HalfOpenTrialCount,
                    CircuitState.Open => false,
                    _ => true
                };
            }
        }

        public bool TryEnter()
        {
            if (!_options.Enabled)
            {
                return true;
            }

            lock (_lock)
            {
                CheckHalfOpenTransitionUnderLock();

                if (_state == CircuitState.Closed)
                {
                    return true;
                }

                if (_state == CircuitState.HalfOpen)
                {
                    if (_halfOpenTrialsIssued < _options.HalfOpenTrialCount)
                    {
                        _halfOpenTrialsIssued++;
                        _logger.LogInformation(
                            "Circuit breaker trial admitted: Dependency={Dependency}, Trial={Trial}/{MaxTrials}",
                            Name, _halfOpenTrialsIssued, _options.HalfOpenTrialCount);
                        return true;
                    }

                    return false;
                }

                return false;
            }
        }

        public void RecordSuccess()
        {
            if (!_options.Enabled)
            {
                return;
            }

            lock (_lock)
            {
                Interlocked.Increment(ref _totalSuccesses);
                _consecutiveFailures = 0;

                if (_state == CircuitState.HalfOpen)
                {
                    _logger.LogInformation("Probe succeeded for {Dependency}. Closing circuit.", Name);
                    TransitionToUnderLock(CircuitState.Closed);
                }
            }
        }

        public void RecordFailure()
        {
            if (!_options.Enabled)
            {
                return;
            }

            lock (_lock)
            {
                Interlocked.Increment(ref _totalFailures);

                if (_state == CircuitState.HalfOpen)
                {
                    _logger.LogWarning("Probe failed for {Dependency} in HalfOpen state. Reopening circuit.", Name);
                    TransitionToUnderLock(CircuitState.Open);
                    return;
                }

                _consecutiveFailures++;
                _logger.LogWarning(
                    "Failure recorded for dependency {Dependency}: ConsecutiveFailures={ConsecutiveFailures}/{Threshold}",
                    Name, _consecutiveFailures, _options.FailureThreshold);

                if (_consecutiveFailures >= _options.FailureThreshold)
                {
                    TransitionToUnderLock(CircuitState.Open);
                }
            }
        }

        public CircuitBreakerSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                CheckHalfOpenTransitionUnderLock();

                DateTime? nextProbe = null;
                if (_state == CircuitState.Open && _openedAtUtc.HasValue)
                {
                    nextProbe = _openedAtUtc.Value + _options.BreakDuration;
                }

                return new CircuitBreakerSnapshot(
                    Name,
                    _state,
                    _consecutiveFailures,
                    _openedAtUtc,
                    nextProbe,
                    _halfOpenTrialsIssued,
                    Interlocked.Read(ref _totalSuccesses),
                    Interlocked.Read(ref _totalFailures));
            }
        }

        private void CheckHalfOpenTransitionUnderLock()
        {
            if (_state == CircuitState.Open && _openedAtUtc.HasValue)
            {
                if (_timeProvider.GetUtcNow().UtcDateTime - _openedAtUtc.Value >= _options.BreakDuration)
                {
                    _logger.LogInformation("Break duration elapsed for {Dependency}. Entering HalfOpen state.", Name);
                    TransitionToUnderLock(CircuitState.HalfOpen);
                }
            }
        }

        private void TransitionToUnderLock(CircuitState newState)
        {
            var oldState = _state;
            if (oldState == newState)
            {
                return;
            }

            _state = newState;

            if (newState == CircuitState.Open)
            {
                _openedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _halfOpenTrialsIssued = 0;
                _metrics?.RecordCircuitOpened();
            }
            else if (newState == CircuitState.HalfOpen)
            {
                _halfOpenTrialsIssued = 0;
                _metrics?.RecordCircuitHalfOpen();
            }
            else if (newState == CircuitState.Closed)
            {
                _consecutiveFailures = 0;
                _openedAtUtc = null;
                _halfOpenTrialsIssued = 0;
                _metrics?.RecordCircuitClosed();
            }

            _logger.LogWarning(
                "Circuit state changed: Dependency={Dependency}, OldState={OldState}, NewState={NewState}",
                Name, oldState, newState);

            if (_scopeFactory is not null)
            {
                var severity = newState == CircuitState.Closed ? AuditSeverity.Info : AuditSeverity.Warning;
                var message = $"Circuit breaker state changed from {oldState} to {newState} for dependency '{Name}'.";

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
                                eventType: AuditEventType.CircuitStateChanged,
                                severity: severity,
                                message: message,
                                outcome: newState.ToString(),
                                actorType: "System");
                        }
                    }
                    catch
                    {
                        // Fire-and-forget audit logging on circuit state transition
                    }
                });
            }
        }
    }
}
