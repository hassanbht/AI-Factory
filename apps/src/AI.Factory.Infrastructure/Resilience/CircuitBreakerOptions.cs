namespace AI.Factory.Infrastructure.Resilience;

public sealed class CircuitBreakerOptions
{
    public int ConsecutiveFailureThreshold { get; set; } = 5;

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many calls are let through while HalfOpen before deciding to Close or re-Open.</summary>
    public int HalfOpenTrialCount { get; set; } = 1;
}
