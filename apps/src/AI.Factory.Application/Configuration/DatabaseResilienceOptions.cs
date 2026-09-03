namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed configuration for database resilience, retries, command timeouts,
/// and optimistic concurrency protection.
/// </summary>
public sealed class DatabaseResilienceOptions
{
    public const string SectionName = "DatabaseResilience";

    public bool EnableRetries { get; set; } = true;

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public bool EnableOptimisticConcurrency { get; set; } = true;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxRetryAttempts < 1 || MaxRetryAttempts > 10)
        {
            throw new InvalidOperationException(
                $"DatabaseResilience:MaxRetryAttempts must be between 1 and 10. Value was '{MaxRetryAttempts}'.");
        }

        if (InitialRetryDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "DatabaseResilience:InitialRetryDelay must be greater than zero.");
        }

        if (MaxRetryDelay < InitialRetryDelay)
        {
            throw new InvalidOperationException(
                $"DatabaseResilience:MaxRetryDelay ({MaxRetryDelay.TotalMilliseconds}ms) must be greater than or equal to InitialRetryDelay ({InitialRetryDelay.TotalMilliseconds}ms).");
        }

        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException(
                $"DatabaseResilience:CommandTimeout must be between 1 second and 10 minutes. Value was '{CommandTimeout.TotalSeconds}' seconds.");
        }
    }
}
