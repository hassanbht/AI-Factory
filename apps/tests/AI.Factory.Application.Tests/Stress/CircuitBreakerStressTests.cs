using AI.Factory.Application.Resilience;
using Xunit;

namespace AI.Factory.Application.Tests.Stress;

/// <summary>
/// Phase 17.4.9 — full circuit breaker lifecycle under concurrency: Closed -> Open,
/// fast rejection, Open -> HalfOpen, HalfOpen -> Closed, HalfOpen -> Open, and
/// repeated cycles checked for state leaks. Uses the real registry-backed
/// <see cref="ICircuitBreaker"/> implementation (same type production dependencies
/// route through via <c>ResilienceExecutor</c>).
/// </summary>
[Trait("Category", "Stress")]
public sealed class CircuitBreakerStressTests
{
    private static ICircuitBreaker MakeBreaker(
        string name, ManualTimeProvider time, int threshold = 2, TimeSpan? breakDuration = null, int halfOpenTrialCount = 1)
    {
        var registry = new CircuitBreakerRegistry(timeProvider: time);
        return registry.GetOrCreate(name, new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = threshold,
            BreakDuration = breakDuration ?? TimeSpan.FromSeconds(30),
            HalfOpenTrialCount = halfOpenTrialCount
        });
    }

    [Fact]
    public void ClosedToOpen_ReachesThreshold_ThenFailsFast()
    {
        var breaker = MakeBreaker("ClosedToOpen", new ManualTimeProvider(), threshold: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(breaker.TryEnter());
            breaker.RecordFailure();
        }

        Assert.Equal(CircuitState.Open, breaker.State);

        // Open: fast rejection, no entry granted.
        Assert.False(breaker.TryEnter());
        Assert.False(breaker.CanExecuteFast());
    }

    [Fact]
    public async Task OpenToHalfOpen_AllowsOnlyConfiguredProbeCount_UnderConcurrentPressure()
    {
        var time = new ManualTimeProvider();
        var breaker = MakeBreaker("HalfOpenProbeLimit", time, threshold: 1, breakDuration: TimeSpan.FromSeconds(30), halfOpenTrialCount: 1);

        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(30));

        // 10 concurrent callers race to enter HalfOpen — exactly 1 probe must be admitted.
        var admitted = 0;
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            if (breaker.TryEnter())
            {
                Interlocked.Increment(ref admitted);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, admitted);
    }

    [Fact]
    public void HalfOpenToClosed_ProbeSucceeds_ResetsFailureCount()
    {
        var time = new ManualTimeProvider();
        var breaker = MakeBreaker("HalfOpenToClosed", time, threshold: 1, breakDuration: TimeSpan.FromSeconds(20));

        breaker.TryEnter();
        breaker.RecordFailure();
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(breaker.TryEnter());
        breaker.RecordSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public void HalfOpenToOpen_ProbeFails_ReopensImmediately_AndRestartsBreakDuration()
    {
        var time = new ManualTimeProvider();
        var breaker = MakeBreaker("HalfOpenToOpen", time, threshold: 1, breakDuration: TimeSpan.FromSeconds(20));

        breaker.TryEnter();
        breaker.RecordFailure();

        time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.NotNull(breaker.GetSnapshot().NextProbeAtUtc);

        // Immediately after re-opening, still fails fast (break duration restarted).
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void RepeatedOpenCloseCycles_NoStateLeak_AcrossManyIterations()
    {
        var time = new ManualTimeProvider();
        var breaker = MakeBreaker("RepeatedCycles", time, threshold: 1, breakDuration: TimeSpan.FromSeconds(15));

        for (var cycle = 0; cycle < 25; cycle++)
        {
            Assert.True(breaker.TryEnter());
            breaker.RecordFailure();
            Assert.Equal(CircuitState.Open, breaker.State);

            time.Advance(TimeSpan.FromSeconds(15));

            Assert.True(breaker.TryEnter());
            breaker.RecordSuccess();
            Assert.Equal(CircuitState.Closed, breaker.State);
        }

        var snapshot = breaker.GetSnapshot();
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(25, snapshot.TotalSuccesses);
        Assert.Equal(25, snapshot.TotalFailures);
    }
}
