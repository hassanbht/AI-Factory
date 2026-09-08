using AI.Factory.Application.Resilience;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Resilience;

/// <summary>
/// One circuit per external provider — construct a separate instance (and
/// register it as a singleton, since state must persist across calls) for
/// each provider that needs independent health tracking.
/// </summary>
public sealed class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly string _providerName;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private int _halfOpenTrialsIssued;
    private DateTime _openedAtUtc;
    private long _totalSuccesses;
    private long _totalFailures;

    public string Name => _providerName;

    public CircuitBreaker(CircuitBreakerOptions options, ILogger<CircuitBreaker> logger, string providerName)
    {
        _options = options;
        _logger = logger;
        _providerName = providerName;
    }

    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    public bool CanExecuteFast()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Open)
            {
                return DateTime.UtcNow - _openedAtUtc >= _options.BreakDuration;
            }

            return true;
        }
    }

    public void RecordSuccess() => OnSuccess();

    public CircuitBreakerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            DateTime? nextProbe = null;
            if (_state == CircuitState.Open)
            {
                nextProbe = _openedAtUtc + _options.BreakDuration;
            }

            return new CircuitBreakerSnapshot(
                _providerName,
                _state,
                _consecutiveFailures,
                _openedAtUtc == default ? null : _openedAtUtc,
                nextProbe,
                _halfOpenTrialsIssued,
                _totalSuccesses,
                _totalFailures);
        }
    }

    /// <summary>
    /// isFailure lets a provider that reports failure via a returned result
    /// rather than a thrown exception (e.g. HermesAgentExecutor, which
    /// already converts expected failures into AgentExecutionResult(Success:
    /// false)) still count against the circuit — otherwise a provider that
    /// never throws would never trip it.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, Func<T, bool>? isFailure = null)
    {
        if (!TryEnter())
        {
            throw new CircuitOpenException(_providerName);
        }

        try
        {
            var result = await operation();

            if (isFailure?.Invoke(result) == true)
            {
                RecordFailure();
            }
            else
            {
                OnSuccess();
            }

            return result;
        }
        catch (Exception) when (RecordFailureAndContinue())
        {
            throw; // unreachable — the filter never returns true, it only observes
        }
    }

    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Open)
            {
                if (DateTime.UtcNow - _openedAtUtc < _options.BreakDuration)
                {
                    return false;
                }

                TransitionTo(CircuitState.HalfOpen);
                _halfOpenTrialsIssued = 0;
            }

            if (_state == CircuitState.HalfOpen)
            {
                if (_halfOpenTrialsIssued >= _options.HalfOpenTrialCount)
                {
                    return false;
                }

                _halfOpenTrialsIssued++;
            }

            return true;
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _totalSuccesses++;
            _consecutiveFailures = 0;

            if (_state == CircuitState.HalfOpen)
            {
                TransitionTo(CircuitState.Closed);
            }
        }
    }

    /// <summary>Always returns false — this is an exception FILTER used purely to observe the failure before it propagates, not to catch it.</summary>
    private bool RecordFailureAndContinue()
    {
        RecordFailure();
        return false;
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _totalFailures++;
            if (_state == CircuitState.HalfOpen)
            {
                // Still broken — re-open immediately rather than waiting out more trials.
                TransitionTo(CircuitState.Open);
                _openedAtUtc = DateTime.UtcNow;
                return;
            }

            _consecutiveFailures++;

            if (_consecutiveFailures >= _options.ConsecutiveFailureThreshold)
            {
                TransitionTo(CircuitState.Open);
                _openedAtUtc = DateTime.UtcNow;
            }
        }
    }

    private void TransitionTo(CircuitState newState)
    {
        if (newState == _state)
        {
            return;
        }

        _logger.LogWarning(
            "Circuit transition: Provider={Provider}, From={FromState}, To={ToState}",
            _providerName, _state, newState);

        _state = newState;
    }
}
