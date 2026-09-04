namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed configuration for the worker's degradation/recovery policy —
/// how many consecutive poll failures push the worker into <c>Degraded</c>,
/// how many consecutive successes are required to recover, and how old a
/// failure streak may be before it no longer counts toward the threshold.
/// </summary>
public sealed class WorkerHealthOptions
{
    public const string SectionName = "WorkerHealth";

    /// <summary>Consecutive poll failures required to transition Ready -> Degraded.</summary>
    public int ConsecutiveFailureThreshold { get; set; } = 3;

    /// <summary>Consecutive poll successes required to transition Degraded -> Ready.</summary>
    public int RecoverySuccessThreshold { get; set; } = 1;

    /// <summary>
    /// If a new failure arrives more than this long after the previous one, the
    /// consecutive-failure streak resets instead of accumulating — an isolated
    /// failure an hour later is not part of the same incident.
    /// </summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (ConsecutiveFailureThreshold < 1)
        {
            throw new InvalidOperationException(
                $"WorkerHealth:ConsecutiveFailureThreshold must be at least 1. Value was '{ConsecutiveFailureThreshold}'.");
        }

        if (RecoverySuccessThreshold < 1)
        {
            throw new InvalidOperationException(
                $"WorkerHealth:RecoverySuccessThreshold must be at least 1. Value was '{RecoverySuccessThreshold}'.");
        }

        if (FailureWindow <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "WorkerHealth:FailureWindow must be greater than zero.");
        }
    }
}
