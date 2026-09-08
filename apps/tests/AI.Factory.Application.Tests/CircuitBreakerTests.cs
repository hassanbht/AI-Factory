using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class CircuitBreakerTests
{
    private static CircuitBreaker MakeBreaker(
        int threshold = 3, TimeSpan? breakDuration = null, int halfOpenTrialCount = 1) =>
        new(
            new CircuitBreakerOptions
            {
                ConsecutiveFailureThreshold = threshold,
                BreakDuration = breakDuration ?? TimeSpan.FromMinutes(1),
                HalfOpenTrialCount = halfOpenTrialCount
            },
            NullLogger<CircuitBreaker>.Instance,
            "TestProvider");

    [Fact]
    public async Task ExecuteAsync_OpensCircuit_AfterConsecutiveFailureThreshold()
    {
        var breaker = MakeBreaker(threshold: 3);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));
        }

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ExecuteAsync_FailsFast_WithoutCallingOperation_WhenCircuitIsOpen()
    {
        var breaker = MakeBreaker(threshold: 1, breakDuration: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        Assert.Equal(CircuitState.Open, breaker.State);

        var called = false;

        await Assert.ThrowsAsync<CircuitOpenException>(() =>
            breaker.ExecuteAsync<int>(() =>
            {
                called = true;
                return Task.FromResult(1);
            }));

        Assert.False(called);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversViaHalfOpen_WhenTrialSucceeds()
    {
        var breaker = MakeBreaker(threshold: 1, breakDuration: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        Assert.Equal(CircuitState.Open, breaker.State);

        await Task.Delay(TimeSpan.FromMilliseconds(30));

        var result = await breaker.ExecuteAsync(() => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task ExecuteAsync_ReopensImmediately_WhenHalfOpenTrialFails()
    {
        var breaker = MakeBreaker(threshold: 1, breakDuration: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        await Task.Delay(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("still broken")));

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotAccumulateFailures_AcrossSuccessfulCalls()
    {
        // 2 failures, then a success, then 2 more failures should NOT open
        // a threshold-of-3 circuit — the success resets the streak.
        var breaker = MakeBreaker(threshold: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        await breaker.ExecuteAsync(() => Task.FromResult(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(() => throw new InvalidOperationException("boom")));

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task ExecuteAsync_TreatsResultPredicateFailure_SameAsThrownException()
    {
        var breaker = MakeBreaker(threshold: 1);

        var result = await breaker.ExecuteAsync(() => Task.FromResult(false), isFailure: r => !r);

        Assert.False(result);
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task CircuitBreakerAgentExecutorDecorator_ReturnsControlledFailure_WhenCircuitOpen()
    {
        var breaker = MakeBreaker(threshold: 1, breakDuration: TimeSpan.FromMinutes(5));

        var failingInner = new FakeAgentExecutor(_ => new AgentExecutionResult(false, null, "docker down", 0));
        var decorator = new CircuitBreakerAgentExecutorDecorator(
            failingInner, breaker, NullLogger<CircuitBreakerAgentExecutorDecorator>.Instance);

        // Trips the circuit (isFailure predicate treats Success=false as a failure).
        await decorator.ExecuteAsync(new AgentExecutionRequest("coder", "model", "prompt", "/work"));
        Assert.Equal(CircuitState.Open, breaker.State);

        var succeedingInner = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "ok", null, 5));
        var decoratorOverOpenCircuit = new CircuitBreakerAgentExecutorDecorator(
            succeedingInner, breaker, NullLogger<CircuitBreakerAgentExecutorDecorator>.Instance);

        var result = await decoratorOverOpenCircuit.ExecuteAsync(
            new AgentExecutionRequest("coder", "model", "prompt", "/work"));

        Assert.False(result.Success);
        Assert.Contains("circuit open", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(succeedingInner.WasCalled);
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult> _respond;

        public FakeAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> respond)
        {
            _respond = respond;
        }

        public bool WasCalled { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(_respond(request));
        }
    }
}
