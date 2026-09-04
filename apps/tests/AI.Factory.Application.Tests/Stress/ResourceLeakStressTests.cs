using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using AI.Factory.Application.Tests.Stress.FailureInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Stress;

/// <summary>
/// Phase 17.4.9 section 13 — resource leak and stability testing: repeated
/// mixed cycles of success / transient-failure-recovery / exhausted-failure /
/// cancellation driven through ONE shared <see cref="ResilienceExecutor"/> +
/// <see cref="CircuitBreakerRegistry"/> + <see cref="BulkheadRegistry"/>. The
/// system must return to a stable state (no leaked permits, no unbounded
/// failure growth) after every cycle.
/// </summary>
[Trait("Category", "Stress")]
public sealed class ResourceLeakStressTests
{
    private sealed class NoopAuditLogger : IAuditLogger
    {
        public Task LogAsync(
            Guid executionId,
            AuditEventType eventType,
            AuditSeverity severity,
            string message,
            Guid? projectId = null,
            Guid? workItemId = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            Guid? correlationId = null,
            Guid? operationId = null,
            Guid? runId = null,
            string? outcome = null,
            string? actorType = null,
            string? actorId = null,
            long? durationMs = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ImmediateDelayProvider : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RepeatedMixedResilienceCycles_ReturnToStableState()
    {
        const string dependencyName = "LeakStressDependency";
        var metrics = new OperationalMetrics();
        var circuitRegistry = new CircuitBreakerRegistry(metrics: metrics);
        var bulkheadRegistry = new BulkheadRegistry(metrics: metrics);

        var contextAccessor = new OperationContextAccessor();
        var scopeFactory = new OperationScopeFactory(contextAccessor, metrics, NullLogger<OperationScopeFactory>.Instance);

        var executor = new ResilienceExecutor(
            new ResilienceOptions(),
            new FailureClassifier(),
            scopeFactory,
            contextAccessor,
            new NoopAuditLogger(),
            metrics,
            NullLogger<ResilienceExecutor>.Instance,
            new ExponentialBackoffCalculator(() => 0.5),
            new ImmediateDelayProvider(),
            circuitRegistry,
            bulkheadRegistry);

        var profile = new ResilienceProfile
        {
            Name = dependencyName,
            Enabled = true,
            MaxRetryAttempts = 1,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreaker = new CircuitBreakerProfileOptions
            {
                Enabled = true,
                FailureThreshold = 3,
                BreakDuration = TimeSpan.FromMilliseconds(20),
                HalfOpenTrialCount = 1
            },
            Bulkhead = new BulkheadProfileOptions
            {
                Enabled = true,
                MaxConcurrentExecutions = 3,
                MaxQueueLength = 5
            }
        };

        for (var cycle = 0; cycle < 30; cycle++)
        {
            await RunCycleAsync(executor, profile, cycle);

            // Let a tripped circuit's break duration elapse before the next cycle
            // relies on it — otherwise every subsequent cycle would fast-reject.
            if (circuitRegistry.TryGet(dependencyName)?.State == CircuitState.Open)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30));
            }
        }

        var bulkheadSnapshot = bulkheadRegistry.TryGet(dependencyName)!.GetSnapshot();
        Assert.Equal(0, bulkheadSnapshot.ActiveExecutions);
        Assert.Equal(0, bulkheadSnapshot.QueuedExecutions);

        var circuit = circuitRegistry.TryGet(dependencyName);
        Assert.NotNull(circuit);
        var circuitSnapshot = circuit!.GetSnapshot();
        Assert.True(circuitSnapshot.ConsecutiveFailures <= profile.CircuitBreaker.FailureThreshold);
        Assert.True(circuit.State is CircuitState.Closed or CircuitState.Open or CircuitState.HalfOpen);
    }

    private static async Task RunCycleAsync(ResilienceExecutor executor, ResilienceProfile profile, int cycle)
    {
        var mode = cycle % 4;

        try
        {
            switch (mode)
            {
                case 0: // immediate success
                    await executor.ExecuteAsync(
                        $"Success{cycle}",
                        _ => Task.FromResult(cycle),
                        profile);
                    break;

                case 1: // transient failure then success
                {
                    var simulator = new DependencyFailureSimulator(
                        new FailureInjectionScenario(FailureMode.TransientFailure),
                        new FailureInjectionScenario(FailureMode.Success));

                    await executor.ExecuteAsync(
                        $"TransientRecovery{cycle}",
                        ct => simulator.ExecuteAsync(cycle, ct),
                        profile);
                    break;
                }

                case 2: // exhausted permanent failure — non-retryable, still trips the breaker
                {
                    var simulator = DependencyFailureSimulator.Always(FailureMode.PermanentFailure);
                    await executor.ExecuteAsync<int>(
                        $"ExhaustedFailure{cycle}",
                        ct => simulator.ExecuteAsync(cycle, ct),
                        profile);
                    break;
                }

                case 3: // caller-cancelled operation
                {
                    using var cts = new CancellationTokenSource();
                    cts.Cancel();
                    await executor.ExecuteAsync<int>(
                        $"Cancelled{cycle}",
                        _ => Task.FromResult(cycle),
                        profile,
                        cts.Token);
                    break;
                }
            }
        }
        catch (Exception ex) when (
            ex is RetryExhaustedException
            or CircuitOpenException
            or BulkheadRejectedException
            or OperationCanceledException
            or SimulatedPermanentFailureException)
        {
            // Expected outcomes for the exhausted-failure, circuit-open, and
            // cancellation cycles — the point of this test is that the shared
            // executor/registries recover cleanly, not that every cycle succeeds.
        }
    }
}
