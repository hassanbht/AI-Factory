using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Stress;

/// <summary>
/// Phase 17.4.9 — retry/timeout behavior under concurrency: many operations
/// racing through the same <see cref="ResilienceExecutor"/> instance, mixing
/// recovered failures, exhausted failures, attempt timeouts, and cancellation
/// during backoff. Fakes follow the pattern in <c>ResilienceExecutorTests</c>.
/// </summary>
[Trait("Category", "Stress")]
public sealed class RetryTimeoutStressTests
{
    private sealed class ImmediateDelayProvider : IDelayProvider
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

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

    private static ResilienceExecutor CreateExecutor(OperationalMetrics metrics, IDelayProvider delayProvider)
    {
        var contextAccessor = new OperationContextAccessor();
        var scopeFactory = new OperationScopeFactory(contextAccessor, metrics, NullLogger<OperationScopeFactory>.Instance);

        return new ResilienceExecutor(
            new ResilienceOptions(),
            new FailureClassifier(),
            scopeFactory,
            contextAccessor,
            new NoopAuditLogger(),
            metrics,
            NullLogger<ResilienceExecutor>.Instance,
            new ExponentialBackoffCalculator(() => 0.5),
            delayProvider);
    }

    [Fact]
    public async Task ConcurrentOperations_RecoverAfterOneTransientFailure_AllSucceed()
    {
        var metrics = new OperationalMetrics();
        var executor = CreateExecutor(metrics, new ImmediateDelayProvider());
        var profile = new ResilienceProfile { MaxRetryAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(1) };

        var tasks = Enumerable.Range(0, 20).Select(i =>
        {
            var callCount = 0;
            return executor.ExecuteAsync(
                $"RecoveredOp{i}",
                ct =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        throw new IOException("simulated transient blip");
                    }
                    return Task.FromResult(i);
                },
                profile);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(20, results.Length);

        var snapshot = metrics.GetSnapshot();
        Assert.True(snapshot.RetryAttempts >= 20);
        Assert.Equal(20, snapshot.RetrySucceeded);
    }

    [Fact]
    public async Task ConcurrentOperations_AlwaysFail_AllThrowRetryExhausted_WithCorrectClassification()
    {
        var metrics = new OperationalMetrics();
        var executor = CreateExecutor(metrics, new ImmediateDelayProvider());
        var profile = new ResilienceProfile { MaxRetryAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1) };

        const int operationCount = 10;

        var tasks = Enumerable.Range(0, operationCount).Select(i => executor.ExecuteAsync<int>(
            $"ExhaustedOp{i}",
            _ => throw new IOException("persistent simulated outage"),
            profile)).ToArray();

        var exceptions = new List<RetryExhaustedException>();
        foreach (var task in tasks)
        {
            var ex = await Assert.ThrowsAsync<RetryExhaustedException>(() => task);
            exceptions.Add(ex);
        }

        Assert.Equal(operationCount, exceptions.Count);
        Assert.All(exceptions, ex => Assert.Equal(FailureCategory.Infrastructure, ex.FailureCategory));

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(operationCount, snapshot.RetryExhausted);
    }

    [Fact]
    public async Task ConcurrentOperations_AttemptTimeoutOnFirstTry_AllRecoverOnSecondAttempt()
    {
        var metrics = new OperationalMetrics();
        var executor = CreateExecutor(metrics, new ImmediateDelayProvider());
        var profile = new ResilienceProfile
        {
            MaxRetryAttempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(50),
            InitialDelay = TimeSpan.FromMilliseconds(1)
        };

        const int operationCount = 8;

        var tasks = Enumerable.Range(0, operationCount).Select(i =>
        {
            var callCount = 0;
            return executor.ExecuteAsync(
                $"AttemptTimeoutOp{i}",
                async ct =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }
                    return "recovered";
                },
                profile);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("recovered", r));

        var snapshot = metrics.GetSnapshot();
        Assert.True(snapshot.AttemptTimeouts >= operationCount);
    }

    [Fact]
    public async Task Cancellation_DuringBackoffDelay_StopsBeforeSecondAttempt()
    {
        var metrics = new OperationalMetrics();
        var executor = CreateExecutor(metrics, DefaultDelayProvider.Instance);
        var profile = new ResilienceProfile
        {
            MaxRetryAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(200)
        };

        using var cts = new CancellationTokenSource();
        var callCount = 0;

        var operationTask = executor.ExecuteAsync<int>(
            "CancelDuringBackoffOp",
            _ =>
            {
                Interlocked.Increment(ref callCount);
                throw new IOException("simulated transient failure triggers backoff");
            },
            profile,
            cts.Token);

        // Cancel while the operation is sleeping in backoff (before the 200ms delay elapses).
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operationTask);

        Assert.Equal(1, callCount);
    }
}
