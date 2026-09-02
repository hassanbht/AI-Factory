namespace AI.Factory.Application.Resilience;

public sealed class ExponentialBackoffCalculator : IBackoffCalculator
{
    public const double DefaultJitterFactor = 0.2; // +/- 20%
    private readonly Func<double> _randomProvider;

    public ExponentialBackoffCalculator(Func<double>? randomProvider = null)
    {
        _randomProvider = randomProvider ?? (() => Random.Shared.NextDouble());
    }

    public TimeSpan CalculateDelay(int attempt, ResilienceProfile profile)
    {
        if (profile.MaxRetryAttempts <= 0 || attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        double baseMs;

        if (profile.BackoffType == BackoffType.Constant)
        {
            baseMs = profile.InitialDelay.TotalMilliseconds;
        }
        else
        {
            // Exponential: InitialDelay * 2^(attempt - 1)
            var exponent = Math.Min(attempt - 1, 30); // Prevent overflow
            baseMs = profile.InitialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        }

        var maxMs = profile.MaxDelay.TotalMilliseconds;
        var cappedMs = Math.Min(baseMs, maxMs);

        if (!profile.UseJitter)
        {
            return TimeSpan.FromMilliseconds(Math.Max(0, cappedMs));
        }

        // Apply jitter: +/- DefaultJitterFactor (e.g. +/- 20%) around the capped delay
        var randomFraction = _randomProvider(); // 0.0 .. 1.0
        var jitterMultiplier = 1.0 + (randomFraction * 2.0 - 1.0) * DefaultJitterFactor;
        var jitteredMs = cappedMs * jitterMultiplier;

        // Ensure delay is >= 0 and <= MaxDelay
        var finalMs = Math.Clamp(jitteredMs, 0, maxMs);

        return TimeSpan.FromMilliseconds(finalMs);
    }
}
