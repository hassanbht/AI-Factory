using System.Diagnostics;

namespace AI.Factory.Api.Tests.LoadTesting;

/// <summary>
/// Runs a <see cref="LoadTestScenario"/> with N concurrent workers, bounded by either a
/// fixed operation count or a wall-clock duration. Test-only helper — not production code.
/// </summary>
public sealed class LoadTestRunner : ILoadTestRunner
{
    public async Task<LoadTestResult> RunAsync(
        LoadTestScenario scenario,
        LoadTestConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.TotalOperations is null && configuration.Duration is null)
        {
            throw new ArgumentException(
                "LoadTestConfiguration requires either TotalOperations or Duration.", nameof(configuration));
        }

        if (configuration.WarmupDuration is { } warmup && warmup > TimeSpan.Zero)
        {
            await RunPhaseAsync(scenario, configuration.Concurrency, null, warmup, null, discard: true, cancellationToken);
        }

        var overallStopwatch = Stopwatch.StartNew();
        var result = await RunPhaseAsync(
            scenario,
            configuration.Concurrency,
            configuration.TotalOperations,
            configuration.Duration,
            configuration.RampUpDuration,
            discard: false,
            cancellationToken);
        overallStopwatch.Stop();

        return BuildResult(result, overallStopwatch.Elapsed);
    }

    private static async Task<PhaseAccumulator> RunPhaseAsync(
        LoadTestScenario scenario,
        int concurrency,
        int? totalOperations,
        TimeSpan? duration,
        TimeSpan? rampUpDuration,
        bool discard,
        CancellationToken cancellationToken)
    {
        var accumulator = new PhaseAccumulator();
        var phaseStopwatch = Stopwatch.StartNew();
        long startedCount = 0;

        bool ShouldStart()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (totalOperations is { } max)
            {
                if (Interlocked.Increment(ref startedCount) > max)
                {
                    Interlocked.Decrement(ref startedCount);
                    return false;
                }

                return true;
            }

            return duration is null || phaseStopwatch.Elapsed < duration;
        }

        async Task WorkerAsync(int workerIndex)
        {
            if (rampUpDuration is { } ramp && ramp > TimeSpan.Zero && concurrency > 1)
            {
                var delay = TimeSpan.FromMilliseconds(ramp.TotalMilliseconds * workerIndex / concurrency);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None);
                }
            }

            while (ShouldStart())
            {
                var startTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    await scenario.Operation(cancellationToken);
                    var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                    if (!discard)
                    {
                        accumulator.RecordSuccess(elapsed);
                    }
                }
                catch (LoadTestRejectedException)
                {
                    if (!discard)
                    {
                        accumulator.RecordRejected();
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!discard)
                    {
                        accumulator.RecordCancelled();
                    }
                }
                catch (Exception ex)
                {
                    if (!discard)
                    {
                        accumulator.RecordFailed(ex);
                    }
                }
            }
        }

        var workers = Enumerable.Range(0, Math.Max(1, concurrency)).Select(WorkerAsync);
        await Task.WhenAll(workers);

        return accumulator;
    }

    private static LoadTestResult BuildResult(PhaseAccumulator accumulator, TimeSpan duration)
    {
        var latencies = accumulator.Latencies;
        latencies.Sort();

        TimeSpan Percentile(double p)
        {
            if (latencies.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var rank = (int)Math.Ceiling(p * latencies.Count) - 1;
            rank = Math.Clamp(rank, 0, latencies.Count - 1);
            return latencies[rank];
        }

        var total = accumulator.SuccessCount + accumulator.FailedCount + accumulator.CancelledCount + accumulator.RejectedCount;
        var seconds = duration.TotalSeconds;

        var metrics = new LoadTestMetrics(
            TotalOperations: total,
            SuccessfulOperations: accumulator.SuccessCount,
            FailedOperations: accumulator.FailedCount,
            CancelledOperations: accumulator.CancelledCount,
            RejectedOperations: accumulator.RejectedCount,
            AverageLatency: latencies.Count == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks((long)latencies.Average(l => l.Ticks)),
            MinLatency: latencies.Count == 0 ? TimeSpan.Zero : latencies[0],
            MaxLatency: latencies.Count == 0 ? TimeSpan.Zero : latencies[^1],
            P50Latency: Percentile(0.50),
            P95Latency: Percentile(0.95),
            P99Latency: Percentile(0.99),
            OperationsPerSecond: seconds > 0 ? total / seconds : 0,
            Duration: duration);

        return new LoadTestResult(metrics, accumulator.Exceptions);
    }

    private sealed class PhaseAccumulator
    {
        private readonly object _lock = new();
        private long _successCount;
        private long _failedCount;
        private long _cancelledCount;
        private long _rejectedCount;
        private readonly List<TimeSpan> _latencies = new();
        private readonly List<Exception> _exceptions = new();

        public long SuccessCount => Interlocked.Read(ref _successCount);
        public long FailedCount => Interlocked.Read(ref _failedCount);
        public long CancelledCount => Interlocked.Read(ref _cancelledCount);
        public long RejectedCount => Interlocked.Read(ref _rejectedCount);
        public List<TimeSpan> Latencies => _latencies;
        public IReadOnlyList<Exception> Exceptions => _exceptions;

        public void RecordSuccess(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _successCount);
            lock (_lock)
            {
                _latencies.Add(elapsed);
            }
        }

        public void RecordFailed(Exception ex)
        {
            Interlocked.Increment(ref _failedCount);
            lock (_lock)
            {
                _exceptions.Add(ex);
            }
        }

        public void RecordCancelled() => Interlocked.Increment(ref _cancelledCount);

        public void RecordRejected() => Interlocked.Increment(ref _rejectedCount);
    }
}
