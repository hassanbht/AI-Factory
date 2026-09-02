namespace AI.Factory.Infrastructure.Resilience;

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Fraction of the computed delay randomized in both directions (0.2 = ±20%).</summary>
    public double JitterFactor { get; set; } = 0.2;
}
