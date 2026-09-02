using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ResilienceDependencyIntegrationTests
{
    private readonly OperationalMetrics _metrics = new();
    private readonly FakeAuditLogger _auditLogger = new();
    private readonly OperationContextAccessor _contextAccessor = new();
    private readonly FailureClassifier _failureClassifier = new();
    private readonly NullLogger<ResilienceExecutor> _logger = NullLogger<ResilienceExecutor>.Instance;
    private readonly IOperationScopeFactory _scopeFactory;

    public ResilienceDependencyIntegrationTests()
    {
        _scopeFactory = new OperationScopeFactory(
            _contextAccessor,
            _metrics,
            NullLogger<OperationScopeFactory>.Instance);
    }

    private ResilienceExecutor CreateExecutor(
        ICircuitBreakerRegistry circuitRegistry,
        IBulkheadRegistry bulkheadRegistry)
    {
        var delayProvider = new ImmediateDelayProvider();

        return new ResilienceExecutor(
            new ResilienceOptions(),
            _failureClassifier,
            _scopeFactory,
            _contextAccessor,
            _auditLogger,
            _metrics,
            _logger,
            new ExponentialBackoffCalculator(),
            delayProvider,
            circuitRegistry,
            bulkheadRegistry);
    }

    [Fact]
    public async Task OpenCircuit_FastRejects_WithoutEnteringBulkhead()
    {
        var circuitRegistry = new CircuitBreakerRegistry(metrics: _metrics);
        var bulkheadRegistry = new BulkheadRegistry(metrics: _metrics);
        var executor = CreateExecutor(circuitRegistry, bulkheadRegistry);

        var profile = new ResilienceProfile
        {
            Name = "FastRejectDep",
            Enabled = true,
            MaxRetryAttempts = 0,
            CircuitBreaker = new CircuitBreakerProfileOptions
            {
                Enabled = true,
                FailureThreshold = 1,
                BreakDuration = TimeSpan.FromMinutes(1)
            },
            Bulkhead = new BulkheadProfileOptions
            {
                Enabled = true,
                MaxConcurrentExecutions = 1,
                MaxQueueLength = 1
            }
        };

        // 1. First execution fails with a provider error, opening circuit
        await Assert.ThrowsAsync<RetryExhaustedException>(async () =>
        {
            await executor.ExecuteAsync("Op1", _ => throw new HttpRequestException("Provider down"), profile);
        });

        var circuit = circuitRegistry.TryGet("FastRejectDep");
        Assert.NotNull(circuit);
        Assert.Equal(CircuitState.Open, circuit.State);

        // 2. Next execution fast-rejects with CircuitOpenException
        var ex = await Assert.ThrowsAsync<CircuitOpenException>(async () =>
        {
            await executor.ExecuteAsync("Op2", _ => Task.FromResult(42), profile);
        });

        Assert.Equal("FastRejectDep", ex.DependencyName);

        // Bulkhead was never entered or queued
        var bulkheadSnapshot = bulkheadRegistry.TryGet("FastRejectDep")!.GetSnapshot();
        Assert.Equal(0, bulkheadSnapshot.ActiveExecutions);
        Assert.Equal(0, bulkheadSnapshot.QueuedExecutions);
    }

    [Fact]
    public async Task BulkheadRejection_DoesNotTripCircuit()
    {
        var circuitRegistry = new CircuitBreakerRegistry(metrics: _metrics);
        var bulkheadRegistry = new BulkheadRegistry(metrics: _metrics);
        var executor = CreateExecutor(circuitRegistry, bulkheadRegistry);

        var profile = new ResilienceProfile
        {
            Name = "BulkheadTripTest",
            Enabled = true,
            CircuitBreaker = new CircuitBreakerProfileOptions
            {
                Enabled = true,
                FailureThreshold = 1
            },
            Bulkhead = new BulkheadProfileOptions
            {
                Enabled = true,
                MaxConcurrentExecutions = 1,
                MaxQueueLength = 0 // rejects immediately when 1 is active
            }
        };

        var releaseTcs = new TaskCompletionSource<bool>();

        // Start active execution holding permit
        var runningTask = Task.Run(async () =>
        {
            await executor.ExecuteAsync("HoldingOp", async _ =>
            {
                await releaseTcs.Task;
                return true;
            }, profile);
        });

        await Task.Delay(30);

        // Second execution should be rejected by bulkhead
        var ex = await Assert.ThrowsAsync<BulkheadRejectedException>(async () =>
        {
            await executor.ExecuteAsync("RejectedOp", _ => Task.FromResult(123), profile);
        });

        Assert.Equal("BulkheadTripTest", ex.DependencyName);

        // Release first execution
        releaseTcs.SetResult(true);
        await runningTask;

        // Circuit breaker must still be Closed! Bulkhead throttling is not dependency unhealth
        var circuit = circuitRegistry.TryGet("BulkheadTripTest");
        Assert.NotNull(circuit);
        Assert.Equal(CircuitState.Closed, circuit.State);
        Assert.Equal(0, circuit.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task OptionB_TransientFailure_ThenRetrySuccess_DoesNotTripCircuit()
    {
        var circuitRegistry = new CircuitBreakerRegistry(metrics: _metrics);
        var bulkheadRegistry = new BulkheadRegistry(metrics: _metrics);
        var executor = CreateExecutor(circuitRegistry, bulkheadRegistry);

        var profile = new ResilienceProfile
        {
            Name = "OptionBDep",
            Enabled = true,
            MaxRetryAttempts = 2,
            CircuitBreaker = new CircuitBreakerProfileOptions
            {
                Enabled = true,
                FailureThreshold = 1 // would trip on 1 failure if attempts were counted individually
            },
            Bulkhead = new BulkheadProfileOptions
            {
                Enabled = true,
                MaxConcurrentExecutions = 5
            }
        };

        var attempt = 0;
        var result = await executor.ExecuteAsync("TransientOp", _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                // Transient network failure on attempt 1
                throw new HttpRequestException("Transient network glitch");
            }

            // Succeeds on attempt 2
            return Task.FromResult("recovered");
        }, profile);

        Assert.Equal("recovered", result);
        Assert.Equal(2, attempt);

        // The logical operation succeeded, so the circuit breaker remains Closed and healthy!
        var circuit = circuitRegistry.TryGet("OptionBDep");
        Assert.NotNull(circuit);
        Assert.Equal(CircuitState.Closed, circuit.State);
        Assert.Equal(0, circuit.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task OptionB_RetriesExhausted_OnProviderFault_TripsCircuit()
    {
        var circuitRegistry = new CircuitBreakerRegistry(metrics: _metrics);
        var bulkheadRegistry = new BulkheadRegistry(metrics: _metrics);
        var executor = CreateExecutor(circuitRegistry, bulkheadRegistry);

        var profile = new ResilienceProfile
        {
            Name = "ExhaustedDep",
            Enabled = true,
            MaxRetryAttempts = 1,
            CircuitBreaker = new CircuitBreakerProfileOptions
            {
                Enabled = true,
                FailureThreshold = 1
            }
        };

        // Operation fails both attempts
        await Assert.ThrowsAsync<RetryExhaustedException>(async () =>
        {
            await executor.ExecuteAsync("FailingOp", _ =>
            {
                throw new HttpRequestException("Persistent provider outage");
            }, profile);
        });

        // Now circuit is Open!
        var circuit = circuitRegistry.TryGet("ExhaustedDep");
        Assert.NotNull(circuit);
        Assert.Equal(CircuitState.Open, circuit.State);
        Assert.Equal(1, circuit.GetSnapshot().ConsecutiveFailures);
    }

    private sealed class ImmediateDelayProvider : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAuditLogger : IAuditLogger
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
}
