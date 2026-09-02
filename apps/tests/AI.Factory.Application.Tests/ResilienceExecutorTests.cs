using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ResilienceExecutorTests
{
    private sealed class ImmediateDelayProvider : IDelayProvider
    {
        public List<TimeSpan> Delays { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = new();

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
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                EventType = eventType,
                Severity = severity,
                Message = message,
                ProjectId = projectId,
                WorkItemId = workItemId,
                CorrelationId = correlationId,
                OperationId = operationId,
                RunId = runId,
                Outcome = outcome,
                ActorType = actorType ?? "System"
            });
            return Task.CompletedTask;
        }
    }

    private readonly FakeAuditLogger _auditLogger = new();
    private readonly OperationalMetrics _metrics = new();
    private readonly OperationContextAccessor _contextAccessor = new();
    private readonly IFailureClassifier _failureClassifier = new FailureClassifier();
    private readonly ImmediateDelayProvider _delayProvider = new();
    private readonly IOperationScopeFactory _scopeFactory;

    public ResilienceExecutorTests()
    {
        _scopeFactory = new OperationScopeFactory(
            _contextAccessor,
            _metrics,
            NullLogger<OperationScopeFactory>.Instance);
    }

    private ResilienceExecutor CreateExecutor(ResilienceProfile? defaultProfile = null)
    {
        var options = new ResilienceOptions();
        if (defaultProfile is not null)
        {
            options.Profiles[defaultProfile.Name] = defaultProfile;
        }

        return new ResilienceExecutor(
            options,
            _failureClassifier,
            _scopeFactory,
            _contextAccessor,
            _auditLogger,
            _metrics,
            NullLogger<ResilienceExecutor>.Instance,
            new ExponentialBackoffCalculator(() => 0.5),
            _delayProvider);
    }

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_NoRetries()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var result = await executor.ExecuteAsync(
            "TestOperation",
            ct =>
            {
                callCount++;
                return Task.FromResult("ok");
            },
            new ResilienceProfile { MaxRetryAttempts = 3 });

        Assert.Equal("ok", result);
        Assert.Equal(1, callCount);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(0, snapshot.RetryAttempts);
        Assert.Equal(0, snapshot.RetrySucceeded);
    }

    [Fact]
    public async Task ExecuteAsync_FailsTransientlyThenSucceeds_RetriesAndRecordsMetrics()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var result = await executor.ExecuteAsync(
            "TransientSuccessOp",
            ct =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new System.IO.IOException("Disk read blip");
                }
                return Task.FromResult(100);
            },
            new ResilienceProfile { MaxRetryAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(10) });

        Assert.Equal(100, result);
        Assert.Equal(2, callCount);
        Assert.Single(_delayProvider.Delays);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.RetryAttempts);
        Assert.Equal(1, snapshot.RetrySucceeded);
        Assert.Equal(0, snapshot.RetryExhausted);

        // Verify audit entries
        Assert.Contains(_auditLogger.Entries, e => e.Outcome == "Started");
        Assert.Contains(_auditLogger.Entries, e => e.Outcome == "Scheduled");
        Assert.Contains(_auditLogger.Entries, e => e.Outcome == "Succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_FailsTransientlyUntilExhausted_ThrowsRetryExhaustedException()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var profile = new ResilienceProfile
        {
            MaxRetryAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(5)
        };

        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(() =>
            executor.ExecuteAsync<int>(
                "ExhaustedOp",
                ct =>
                {
                    callCount++;
                    throw new System.IO.IOException("Persistent network outage");
                },
                profile));

        // 1 initial + 2 retries = 3 total calls
        Assert.Equal(3, callCount);
        Assert.Equal(3, ex.AttemptsPerformed);
        Assert.Equal(FailureCategory.Infrastructure, ex.FailureCategory);
        Assert.Equal(2, _delayProvider.Delays.Count);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(2, snapshot.RetryAttempts);
        Assert.Equal(0, snapshot.RetrySucceeded);
        Assert.Equal(1, snapshot.RetryExhausted);

        Assert.Contains(_auditLogger.Entries, e => e.Outcome == "Exhausted");
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientFailure_DoesNotRetry()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var profile = new ResilienceProfile { MaxRetryAttempts = 3 };

        await Assert.ThrowsAsync<ValidationException>(() =>
            executor.ExecuteAsync<int>(
                "ValidationOp",
                ct =>
                {
                    callCount++;
                    throw new ValidationException("Invalid email format");
                },
                profile));

        // Only 1 attempt, zero retries
        Assert.Equal(1, callCount);
        Assert.Empty(_delayProvider.Delays);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(0, snapshot.RetryAttempts);
        Assert.Equal(0, snapshot.RetryExhausted);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownException_DoesNotRetryConservatively()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var profile = new ResilienceProfile { MaxRetryAttempts = 3 };

        await Assert.ThrowsAsync<FormatException>(() =>
            executor.ExecuteAsync<int>(
                "UnknownOp",
                ct =>
                {
                    callCount++;
                    throw new FormatException("weird parse error");
                },
                profile));

        Assert.Equal(1, callCount);
        Assert.Empty(_delayProvider.Delays);
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancelledBeforeExecution_ThrowsOperationCanceledException()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<string>(
                "CancelledOp",
                ct =>
                {
                    callCount++;
                    return Task.FromResult("x");
                },
                cts.Token));

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancelledDuringExecution_DoesNotRetry()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync<string>(
                "CancelDuringOp",
                ct =>
                {
                    callCount++;
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    return Task.FromResult("x");
                },
                new ResilienceProfile { MaxRetryAttempts = 3 },
                cts.Token));

        Assert.Equal(1, callCount);
        Assert.Empty(_delayProvider.Delays);

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.RetryCancelled);
        Assert.Equal(0, snapshot.RetryAttempts);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptTimeout_FailsAttemptAndRetries()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var profile = new ResilienceProfile
        {
            MaxRetryAttempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(50),
            InitialDelay = TimeSpan.FromMilliseconds(5)
        };

        var result = await executor.ExecuteAsync(
            "AttemptTimeoutOp",
            async ct =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Hang until attempt timeout triggers
                    await Task.Delay(2000, ct);
                }
                return "recovered";
            },
            profile);

        Assert.Equal("recovered", result);
        Assert.Equal(2, callCount);

        var snapshot = _metrics.GetSnapshot();
        Assert.True(snapshot.AttemptTimeouts >= 1);
        Assert.Equal(1, snapshot.RetryAttempts);
        Assert.Equal(1, snapshot.RetrySucceeded);
    }

    [Fact]
    public async Task ExecuteAsync_OverallTimeout_StopsRetryingAndThrows()
    {
        var executor = CreateExecutor();
        var callCount = 0;

        var profile = new ResilienceProfile
        {
            MaxRetryAttempts = 5,
            OperationTimeout = TimeSpan.FromMilliseconds(80),
            InitialDelay = TimeSpan.FromMilliseconds(50)
        };

        var realDelayExecutor = new ResilienceExecutor(
            new ResilienceOptions(),
            _failureClassifier,
            _scopeFactory,
            _contextAccessor,
            _auditLogger,
            _metrics,
            NullLogger<ResilienceExecutor>.Instance,
            new ExponentialBackoffCalculator(() => 0.5),
            DefaultDelayProvider.Instance);

        await Assert.ThrowsAsync<OperationTimeoutException>(() =>
            realDelayExecutor.ExecuteAsync<string>(
                "OverallTimeoutOp",
                async ct =>
                {
                    callCount++;
                    await Task.Delay(30, ct);
                    throw new System.IO.IOException("Transient error");
                },
                profile));

        var snapshot = _metrics.GetSnapshot();
        Assert.Equal(1, snapshot.OperationTimeouts);
        Assert.Contains(_auditLogger.Entries, e => e.Outcome == "TimedOut");
    }

    [Fact]
    public async Task ExecuteAsync_PreservesCorrelationHierarchy_AcrossRetries()
    {
        var parentCorrelationId = Guid.NewGuid();
        var parentProjectId = Guid.NewGuid();
        var parentTaskId = Guid.NewGuid();

        await using var parentScope = _scopeFactory.Begin(
            "ParentWorkflow",
            correlationId: parentCorrelationId,
            projectId: parentProjectId,
            taskId: parentTaskId);

        var executor = CreateExecutor();
        var callCount = 0;

        await executor.ExecuteAsync(
            "ChildOperation",
            ct =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new System.IO.IOException("transient");
                }
                return Task.FromResult(true);
            },
            new ResilienceProfile { MaxRetryAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1) });

        // Assert all audit entries carry parent correlation ID and project ID
        Assert.All(_auditLogger.Entries, entry =>
        {
            Assert.Equal(parentCorrelationId, entry.CorrelationId);
            Assert.Equal(parentProjectId, entry.ProjectId);
        });
    }
}
