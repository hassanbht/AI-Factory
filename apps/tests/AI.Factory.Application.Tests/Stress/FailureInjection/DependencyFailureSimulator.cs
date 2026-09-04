namespace AI.Factory.Application.Tests.Stress.FailureInjection;

/// <summary>
/// Deterministic failure modes a simulated dependency can produce, used to
/// drive resilience components (retry, circuit breaker, bulkhead) through
/// known-bad conditions without touching a real external system.
/// </summary>
public enum FailureMode
{
    Success,
    TransientFailure,
    PermanentFailure,
    Timeout,
    Cancellation,
    DelayedSuccess,
    DelayedFailure
}

/// <summary>Exception thrown by a simulated dependency in TransientFailure mode — classified as transient by <c>FailureClassifier</c> (an <see cref="IOException"/>-family error).</summary>
public sealed class SimulatedTransientFailureException : IOException
{
    public SimulatedTransientFailureException(string message) : base(message)
    {
    }
}

/// <summary>Exception thrown by a simulated dependency in PermanentFailure mode — never retried.</summary>
public sealed class SimulatedPermanentFailureException : Exception
{
    public SimulatedPermanentFailureException(string message) : base(message)
    {
    }
}

/// <summary>
/// A single scripted step for <see cref="DependencyFailureSimulator"/> —
/// pairs a failure mode with an optional delay (used by Timeout/DelayedSuccess/DelayedFailure).
/// </summary>
public sealed record FailureInjectionScenario(FailureMode Mode, TimeSpan? Delay = null);

/// <summary>
/// Plays back a scripted sequence of <see cref="FailureInjectionScenario"/> steps,
/// one per invocation — the Nth call to <see cref="ExecuteAsync{T}"/> executes
/// the Nth scripted step. Once the script is exhausted, the last step repeats.
/// </summary>
public sealed class DependencyFailureSimulator
{
    private readonly IReadOnlyList<FailureInjectionScenario> _script;
    private int _callCount;

    public DependencyFailureSimulator(params FailureInjectionScenario[] script)
    {
        if (script.Length == 0)
        {
            throw new ArgumentException("Script must contain at least one scenario.", nameof(script));
        }

        _script = script;
    }

    public int CallCount => _callCount;

    public static DependencyFailureSimulator Always(FailureMode mode, TimeSpan? delay = null) =>
        new(new FailureInjectionScenario(mode, delay));

    public async Task<T> ExecuteAsync<T>(T successValue, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _callCount) - 1;
        var scenario = _script[Math.Min(index, _script.Count - 1)];

        switch (scenario.Mode)
        {
            case FailureMode.Success:
                return successValue;

            case FailureMode.TransientFailure:
                throw new SimulatedTransientFailureException("Simulated transient dependency failure.");

            case FailureMode.PermanentFailure:
                throw new SimulatedPermanentFailureException("Simulated permanent dependency failure.");

            case FailureMode.Timeout:
                await Task.Delay(scenario.Delay ?? TimeSpan.FromSeconds(5), cancellationToken);
                return successValue;

            case FailureMode.Cancellation:
                throw new OperationCanceledException("Simulated dependency-initiated cancellation.");

            case FailureMode.DelayedSuccess:
                await Task.Delay(scenario.Delay ?? TimeSpan.FromMilliseconds(10), cancellationToken);
                return successValue;

            case FailureMode.DelayedFailure:
                await Task.Delay(scenario.Delay ?? TimeSpan.FromMilliseconds(10), cancellationToken);
                throw new SimulatedTransientFailureException("Simulated delayed transient dependency failure.");

            default:
                throw new NotSupportedException($"Unsupported failure mode: {scenario.Mode}");
        }
    }
}
