using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class CircuitBreakerRegistryTests
{
    private readonly OperationalMetrics _metrics = new();

    [Fact]
    public void GetOrCreate_ReturnsSameInstance_ForSameNameCaseInsensitive()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);

        var cb1 = registry.GetOrCreate("OpenAI");
        var cb2 = registry.GetOrCreate("openai");

        Assert.Same(cb1, cb2);
        Assert.Equal("OpenAI", cb1.Name);
    }

    [Fact]
    public void StartsClosed_And_CanExecuteFastIsTrue()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);
        var cb = registry.GetOrCreate("TestDep", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 3,
            BreakDuration = TimeSpan.FromSeconds(5)
        });

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.CanExecuteFast());
        Assert.True(cb.TryEnter());
    }

    [Fact]
    public void ConsecutiveFailures_ReachThreshold_OpensCircuit()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);
        var cb = registry.GetOrCreate("TestDep", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 2,
            BreakDuration = TimeSpan.FromSeconds(10)
        });

        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.CanExecuteFast());

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.CanExecuteFast());
        Assert.False(cb.TryEnter());

        var snapshot = cb.GetSnapshot();
        Assert.Equal(CircuitState.Open, snapshot.State);
        Assert.Equal(2, snapshot.ConsecutiveFailures);
        Assert.NotNull(snapshot.OpenedAtUtc);
        Assert.NotNull(snapshot.NextProbeAtUtc);
    }

    [Fact]
    public void Success_ResetsConsecutiveFailures_WhenClosed()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);
        var cb = registry.GetOrCreate("TestDep", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 3
        });

        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(2, cb.GetSnapshot().ConsecutiveFailures);

        cb.RecordSuccess();
        Assert.Equal(0, cb.GetSnapshot().ConsecutiveFailures);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public void Open_RemainsOpen_BeforeBreakDurationExpires()
    {
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);
        var cb = registry.GetOrCreate("StillOpen", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            HalfOpenTrialCount = 1
        });

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // One tick short of the break duration — must not transition yet.
        time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.CanExecuteFast());
        Assert.False(cb.TryEnter());
    }

    [Fact]
    public void BreakDurationElapses_TransitionsToHalfOpen_AllowsTrials()
    {
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);
        var cb = registry.GetOrCreate("QuickBreak", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            HalfOpenTrialCount = 1
        });

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.CanExecuteFast());

        // Advance the deterministic clock past the break duration — no real waiting.
        time.Advance(TimeSpan.FromSeconds(30));

        // State changes to HalfOpen upon probe evaluation
        Assert.True(cb.CanExecuteFast());
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // First trial admitted
        Assert.True(cb.TryEnter());

        // Second trial rejected (trial limit reached)
        Assert.False(cb.TryEnter());
    }

    [Fact]
    public void HalfOpen_SuccessClosesCircuit()
    {
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);
        var cb = registry.GetOrCreate("ProbeSuccess", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            HalfOpenTrialCount = 1
        });

        cb.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(cb.TryEnter());
        cb.RecordSuccess();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.GetSnapshot().ConsecutiveFailures);
        Assert.True(cb.CanExecuteFast());
    }

    [Fact]
    public void HalfOpen_FailureReopensCircuitImmediately()
    {
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);
        var cb = registry.GetOrCreate("ProbeFailure", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 5, // high threshold, but probe failure must reopen on 1 failure
            BreakDuration = TimeSpan.FromSeconds(30),
            HalfOpenTrialCount = 1
        });

        // Open manually
        for (var i = 0; i < 5; i++)
        {
            cb.RecordFailure();
        }
        Assert.Equal(CircuitState.Open, cb.State);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cb.TryEnter());
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Probe fails
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.CanExecuteFast());
    }

    [Fact]
    public void IndependentDependencies_MaintainIndependentState_OnSharedClock()
    {
        // The registry's clock is shared across breakers (one TimeProvider per registry), but each
        // named breaker's failure/state tracking must remain fully independent of the others.
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);

        var depA = registry.GetOrCreate("DepA", new CircuitBreakerProfileOptions { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(10) });
        var depB = registry.GetOrCreate("DepB", new CircuitBreakerProfileOptions { FailureThreshold = 3, BreakDuration = TimeSpan.FromSeconds(10) });

        depA.RecordFailure();
        Assert.Equal(CircuitState.Open, depA.State);
        Assert.Equal(CircuitState.Closed, depB.State);

        depB.RecordFailure();
        depB.RecordFailure();
        Assert.Equal(CircuitState.Closed, depB.State);

        time.Advance(TimeSpan.FromSeconds(10));

        // DepA's break has elapsed and transitions independently of DepB, which never opened.
        Assert.Equal(CircuitState.HalfOpen, depA.State);
        Assert.Equal(CircuitState.Closed, depB.State);
        Assert.Equal(2, depB.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public void ConcurrentCallers_AtTransitionBoundary_RemainThreadSafe()
    {
        var time = new ManualTimeProvider();
        var registry = new CircuitBreakerRegistry(metrics: _metrics, timeProvider: time);
        var cb = registry.GetOrCreate("BoundaryDep", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromSeconds(10),
            HalfOpenTrialCount = 3
        });

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);

        // Advance exactly to the boundary, then hammer TryEnter concurrently — no real delay
        // anywhere in this test, and the shared lock must keep half-open admission bounded.
        time.Advance(TimeSpan.FromSeconds(10));

        var admitted = 0;
        Parallel.For(0, 50, _ =>
        {
            if (cb.TryEnter())
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(3, admitted);
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    [Fact]
    public void DisabledBreaker_AlwaysAllowsExecution()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);
        var cb = registry.GetOrCreate("Disabled", new CircuitBreakerProfileOptions
        {
            Enabled = false,
            FailureThreshold = 1
        });

        for (var i = 0; i < 10; i++)
        {
            cb.RecordFailure();
        }

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.CanExecuteFast());
        Assert.True(cb.TryEnter());
    }

    [Fact]
    public async Task ConcurrentSafety_MultipleThreadsRecordingOutcomes()
    {
        var registry = new CircuitBreakerRegistry(metrics: _metrics);
        var cb = registry.GetOrCreate("ConcurrentDep", new CircuitBreakerProfileOptions
        {
            Enabled = true,
            FailureThreshold = 50,
            BreakDuration = TimeSpan.FromSeconds(5)
        });

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                if (cb.CanExecuteFast())
                {
                    cb.RecordSuccess();
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var snapshot = cb.GetSnapshot();
        Assert.Equal(CircuitState.Closed, snapshot.State);
        Assert.Equal(5000, snapshot.TotalSuccesses);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }
}
