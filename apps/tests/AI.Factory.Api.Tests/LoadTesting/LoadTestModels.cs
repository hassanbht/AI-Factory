namespace AI.Factory.Api.Tests.LoadTesting;

/// <summary>
/// Marker exception a <see cref="LoadTestScenario"/> operation can throw to signal a
/// controlled rejection (e.g. HTTP 429/503) rather than an unexpected failure.
/// </summary>
public sealed class LoadTestRejectedException : Exception
{
    public LoadTestRejectedException(string message) : base(message)
    {
    }
}

public sealed record LoadTestConfiguration
{
    public required int Concurrency { get; init; }

    /// <summary>Either this or <see cref="Duration"/> must be set to bound the run.</summary>
    public int? TotalOperations { get; init; }

    public TimeSpan? Duration { get; init; }

    public TimeSpan? WarmupDuration { get; init; }

    public TimeSpan? RampUpDuration { get; init; }
}

public sealed record LoadTestMetrics(
    long TotalOperations,
    long SuccessfulOperations,
    long FailedOperations,
    long CancelledOperations,
    long RejectedOperations,
    TimeSpan AverageLatency,
    TimeSpan MinLatency,
    TimeSpan MaxLatency,
    TimeSpan P50Latency,
    TimeSpan P95Latency,
    TimeSpan P99Latency,
    double OperationsPerSecond,
    TimeSpan Duration);

public sealed record LoadTestResult(
    LoadTestMetrics Metrics,
    IReadOnlyList<Exception> Exceptions);

public sealed record LoadTestScenario(
    string Name,
    Func<CancellationToken, Task> Operation);

public interface ILoadTestRunner
{
    Task<LoadTestResult> RunAsync(
        LoadTestScenario scenario,
        LoadTestConfiguration configuration,
        CancellationToken cancellationToken = default);
}
