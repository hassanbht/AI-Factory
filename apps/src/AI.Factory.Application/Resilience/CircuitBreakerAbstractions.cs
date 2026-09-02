namespace AI.Factory.Application.Resilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public sealed class CircuitBreakerProfileOptions
{
    public bool Enabled { get; set; } = true;

    public int FailureThreshold { get; set; } = 5;

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    public int HalfOpenTrialCount { get; set; } = 1;

    public TimeSpan? SamplingWindow { get; set; }

    public CircuitBreakerProfileOptions Clone()
    {
        return new CircuitBreakerProfileOptions
        {
            Enabled = Enabled,
            FailureThreshold = FailureThreshold,
            BreakDuration = BreakDuration,
            HalfOpenTrialCount = HalfOpenTrialCount,
            SamplingWindow = SamplingWindow
        };
    }
}

public sealed record CircuitBreakerSnapshot(
    string DependencyName,
    CircuitState State,
    int ConsecutiveFailures,
    DateTime? OpenedAtUtc,
    DateTime? NextProbeAtUtc,
    int HalfOpenTrialsIssued,
    long TotalSuccesses,
    long TotalFailures);

public interface ICircuitBreaker
{
    string Name { get; }

    CircuitState State { get; }

    bool CanExecuteFast();

    bool TryEnter();

    void RecordSuccess();

    void RecordFailure();

    CircuitBreakerSnapshot GetSnapshot();
}

public interface ICircuitBreakerRegistry
{
    ICircuitBreaker GetOrCreate(string name, CircuitBreakerProfileOptions? options = null);

    ICircuitBreaker? TryGet(string name);

    IReadOnlyCollection<CircuitBreakerSnapshot> GetAllSnapshots();
}

public class CircuitOpenException : Exception
{
    public string DependencyName { get; }

    public CircuitState State { get; }

    public DateTime? NextProbeAtUtc { get; }

    public CircuitOpenException(string dependencyName, CircuitState state = CircuitState.Open, DateTime? nextProbeAtUtc = null)
        : base($"Dependency '{dependencyName}' is currently unavailable (circuit {state}).")
    {
        DependencyName = dependencyName;
        State = state;
        NextProbeAtUtc = nextProbeAtUtc;
    }
}
