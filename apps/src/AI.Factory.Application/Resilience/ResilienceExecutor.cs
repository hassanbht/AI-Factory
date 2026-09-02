using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Resilience;

public sealed class ResilienceExecutor : IResilienceExecutor
{
    private static readonly HashSet<FailureCategory> DefaultNonRetryableCategories = new()
    {
        FailureCategory.Validation,
        FailureCategory.Approval,
        FailureCategory.Review,
        FailureCategory.Cancelled,
        FailureCategory.Unknown
    };

    private readonly ResilienceOptions _options;
    private readonly IFailureClassifier _failureClassifier;
    private readonly IOperationScopeFactory _scopeFactory;
    private readonly IOperationContextAccessor _contextAccessor;
    private readonly IAuditLogger _auditLogger;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<ResilienceExecutor> _logger;
    private readonly IBackoffCalculator _backoffCalculator;
    private readonly IDelayProvider _delayProvider;
    private readonly ICircuitBreakerRegistry _circuitBreakerRegistry;
    private readonly IBulkheadRegistry _bulkheadRegistry;

    public ResilienceExecutor(
        ResilienceOptions options,
        IFailureClassifier failureClassifier,
        IOperationScopeFactory scopeFactory,
        IOperationContextAccessor contextAccessor,
        IAuditLogger auditLogger,
        IOperationalMetrics metrics,
        ILogger<ResilienceExecutor> logger,
        IBackoffCalculator? backoffCalculator = null,
        IDelayProvider? delayProvider = null,
        ICircuitBreakerRegistry? circuitBreakerRegistry = null,
        IBulkheadRegistry? bulkheadRegistry = null)
    {
        _options = options ?? new ResilienceOptions();
        _failureClassifier = failureClassifier;
        _scopeFactory = scopeFactory;
        _contextAccessor = contextAccessor;
        _auditLogger = auditLogger;
        _metrics = metrics;
        _logger = logger;
        _backoffCalculator = backoffCalculator ?? new ExponentialBackoffCalculator();
        _delayProvider = delayProvider ?? DefaultDelayProvider.Instance;
        _circuitBreakerRegistry = circuitBreakerRegistry ?? new CircuitBreakerRegistry(null, metrics);
        _bulkheadRegistry = bulkheadRegistry ?? new BulkheadRegistry(null, metrics);
    }

    public Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var profile = _options.GetProfile(operationName);
        return ExecuteAsync(operationName, operation, profile, cancellationToken);
    }

    public Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        var profile = _options.GetProfile(operationName);
        return ExecuteAsync(operationName, operation, profile, cancellationToken);
    }

    public async Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        ResilienceProfile profile,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<bool>(
            operationName,
            async ct =>
            {
                await operation(ct);
                return true;
            },
            profile,
            cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        ResilienceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(profile);

        // Caller cancellation token always takes strict precedence
        cancellationToken.ThrowIfCancellationRequested();

        if (!profile.Enabled)
        {
            return await operation(cancellationToken);
        }

        var dependencyName = !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name : operationName;
        var circuit = _circuitBreakerRegistry.GetOrCreate(dependencyName, profile.CircuitBreaker);
        var bulkhead = _bulkheadRegistry.GetOrCreate(dependencyName, profile.Bulkhead);

        // 1. Pipeline Step 1: Circuit breaker fast check (fail fast without queuing or consuming permits)
        if (profile.CircuitBreaker is not null && profile.CircuitBreaker.Enabled && !circuit.CanExecuteFast())
        {
            _metrics.RecordCircuitRejected();
            var snapshot = circuit.GetSnapshot();
            var ambientContext = _contextAccessor.Current;
            var parentCorrelationId = ambientContext?.CorrelationId ?? Guid.NewGuid();

            await RecordAuditAsync(
                parentCorrelationId,
                ambientContext,
                AuditEventType.CircuitRejected,
                AuditSeverity.Warning,
                $"Operation '{operationName}' rejected by open circuit breaker for '{dependencyName}'.",
                outcome: "Rejected",
                cancellationToken: CancellationToken.None);

            throw new CircuitOpenException(dependencyName, snapshot.State, snapshot.NextProbeAtUtc);
        }

        // 2. Pipeline Step 2: Bulkhead isolation (acquire concurrency permit or queue up to MaxQueueLength)
        using var bulkheadPermit = await bulkhead.AcquireAsync(cancellationToken);

        // 3. Pipeline Step 3: Circuit breaker trial admission (Half-Open Gate)
        if (profile.CircuitBreaker is not null && profile.CircuitBreaker.Enabled && !circuit.TryEnter())
        {
            _metrics.RecordCircuitRejected();
            var snapshot = circuit.GetSnapshot();
            var ambientContext = _contextAccessor.Current;
            var parentCorrelationId = ambientContext?.CorrelationId ?? Guid.NewGuid();

            await RecordAuditAsync(
                parentCorrelationId,
                ambientContext,
                AuditEventType.CircuitRejected,
                AuditSeverity.Warning,
                $"Operation '{operationName}' rejected by circuit breaker trial limit for '{dependencyName}'.",
                outcome: "Rejected",
                cancellationToken: CancellationToken.None);

            throw new CircuitOpenException(dependencyName, snapshot.State, snapshot.NextProbeAtUtc);
        }

        // 4. Pipeline Step 4: Retry / Timeout Attempt Loop (observing final logical result for Circuit Breaker)
        try
        {
            var result = await ExecuteCoreAsync(operationName, operation, profile, cancellationToken);
            circuit.RecordSuccess();
            return result;
        }
        catch (Exception ex)
        {
            if (ShouldTripCircuit(ex, profile))
            {
                circuit.RecordFailure();
            }

            throw;
        }
    }

    private async Task<T> ExecuteCoreAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        ResilienceProfile profile,
        CancellationToken cancellationToken)
    {
        var maxAttempts = 1 + Math.Max(0, profile.MaxRetryAttempts);
        var ambientContext = _contextAccessor.Current;
        var parentCorrelationId = ambientContext?.CorrelationId ?? Guid.NewGuid();

        // Configure overall operation timeout if specified
        using var overallCts = profile.OperationTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (overallCts is not null)
        {
            overallCts.CancelAfter(profile.OperationTimeout!.Value);
        }

        var effectiveToken = overallCts?.Token ?? cancellationToken;
        var attempt = 0;

        while (true)
        {
            attempt++;

            // Check caller cancellation before each attempt
            if (cancellationToken.IsCancellationRequested)
            {
                _metrics.RecordRetryCancelled();
                await RecordAuditAsync(
                    parentCorrelationId,
                    ambientContext,
                    AuditEventType.Retry,
                    AuditSeverity.Warning,
                    $"Operation '{operationName}' cancelled by caller before attempt {attempt}.",
                    outcome: "Cancelled",
                    cancellationToken: CancellationToken.None);

                cancellationToken.ThrowIfCancellationRequested();
            }

            // Check overall timeout before each attempt
            if (overallCts is not null && overallCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _metrics.RecordOperationTimeout();
                await RecordAuditAsync(
                    parentCorrelationId,
                    ambientContext,
                    AuditEventType.Timeout,
                    AuditSeverity.Error,
                    $"Operation '{operationName}' exceeded overall timeout of {profile.OperationTimeout}.",
                    outcome: "TimedOut",
                    cancellationToken: CancellationToken.None);

                throw new OperationTimeoutException(operationName, profile.OperationTimeout!.Value);
            }

            // Open a child operation scope for this attempt
            await using var attemptScope = _scopeFactory.Begin(
                $"{operationName}.Attempt",
                correlationId: parentCorrelationId,
                projectId: ambientContext?.ProjectId,
                taskId: ambientContext?.TaskId,
                runId: ambientContext?.RunId);

            var attemptContext = attemptScope.Context;
            var attemptStopwatch = Stopwatch.StartNew();

            await RecordAuditAsync(
                parentCorrelationId,
                attemptContext,
                AuditEventType.Retry,
                AuditSeverity.Info,
                $"Attempt {attempt}/{maxAttempts} started for operation '{operationName}'.",
                outcome: "Started",
                cancellationToken: cancellationToken);

            // Configure attempt timeout
            using var attemptCts = profile.AttemptTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(effectiveToken)
                : null;

            if (attemptCts is not null)
            {
                attemptCts.CancelAfter(profile.AttemptTimeout!.Value);
            }

            var attemptToken = attemptCts?.Token ?? effectiveToken;

            try
            {
                var result = await operation(attemptToken);
                attemptStopwatch.Stop();

                if (attempt > 1)
                {
                    _metrics.RecordRetrySuccess();
                }

                await RecordAuditAsync(
                    parentCorrelationId,
                    attemptContext,
                    AuditEventType.Retry,
                    AuditSeverity.Info,
                    $"Attempt {attempt}/{maxAttempts} for '{operationName}' succeeded in {attemptStopwatch.ElapsedMilliseconds}ms.",
                    outcome: "Succeeded",
                    durationMs: attemptStopwatch.ElapsedMilliseconds,
                    cancellationToken: cancellationToken);

                return result;
            }
            catch (Exception ex)
            {
                attemptStopwatch.Stop();
                var durationMs = attemptStopwatch.ElapsedMilliseconds;

                // 1. Caller cancelled
                if (cancellationToken.IsCancellationRequested)
                {
                    attemptScope.Fail("Caller cancelled");
                    _metrics.RecordRetryCancelled();

                    await RecordAuditAsync(
                        parentCorrelationId,
                        attemptContext,
                        AuditEventType.Retry,
                        AuditSeverity.Warning,
                        $"Operation '{operationName}' cancelled by caller during attempt {attempt}.",
                        outcome: "Cancelled",
                        durationMs: durationMs,
                        cancellationToken: CancellationToken.None);

                    throw;
                }

                // 2. Overall operation timeout expired
                if (overallCts is not null && overallCts.IsCancellationRequested)
                {
                    attemptScope.Fail("Overall operation timeout expired");
                    _metrics.RecordOperationTimeout();

                    await RecordAuditAsync(
                        parentCorrelationId,
                        attemptContext,
                        AuditEventType.Timeout,
                        AuditSeverity.Error,
                        $"Operation '{operationName}' timed out after {attempt} attempt(s) (overall timeout {profile.OperationTimeout}).",
                        outcome: "TimedOut",
                        durationMs: durationMs,
                        cancellationToken: CancellationToken.None);

                    throw new OperationTimeoutException(operationName, profile.OperationTimeout!.Value);
                }

                // 3. Attempt timeout expired
                Exception failureException = ex;
                if (attemptCts is not null && attemptCts.IsCancellationRequested)
                {
                    _metrics.RecordAttemptTimeout();
                    failureException = new AttemptTimeoutException(operationName, attempt, profile.AttemptTimeout!.Value);
                    attemptScope.Fail(failureException);
                }
                else
                {
                    attemptScope.Fail(ex);
                }

                // Classify failure
                var classification = _failureClassifier.Classify(failureException);

                await RecordAuditAsync(
                    parentCorrelationId,
                    attemptContext,
                    AuditEventType.Retry,
                    AuditSeverity.Warning,
                    $"Attempt {attempt}/{maxAttempts} for '{operationName}' failed ({classification.Category}): {classification.Summary}",
                    outcome: "Failed",
                    failureCategory: classification.Category,
                    durationMs: durationMs,
                    cancellationToken: CancellationToken.None);

                // Determine retry eligibility
                var canRetry = ShouldRetry(attempt, maxAttempts, classification, profile);

                if (!canRetry)
                {
                    if (attempt >= maxAttempts && IsTransientFailure(classification, profile))
                    {
                        _metrics.RecordRetryExhausted();

                        await RecordAuditAsync(
                            parentCorrelationId,
                            attemptContext,
                            AuditEventType.Retry,
                            AuditSeverity.Error,
                            $"Operation '{operationName}' exhausted all {maxAttempts} attempt(s). Final failure ({classification.Category}): {classification.Summary}",
                            outcome: "Exhausted",
                            failureCategory: classification.Category,
                            durationMs: durationMs,
                            cancellationToken: CancellationToken.None);

                        throw new RetryExhaustedException(operationName, attempt, classification.Category, failureException);
                    }

                    // Permanent / non-retryable failure: propagate immediately
                    throw failureException;
                }

                // Compute delay
                var delay = _backoffCalculator.CalculateDelay(attempt, profile);

                _metrics.RecordRetryAttempt();

                _logger.LogWarning(
                    "Retry scheduled: Operation={OperationName}, Attempt={AttemptNumber}, MaxAttempts={MaxAttempts}, DelayMs={DelayMs}, Category={Category}, IsTransient={IsTransient}, CorrelationId={CorrelationId}",
                    operationName, attempt, maxAttempts, delay.TotalMilliseconds, classification.Category, classification.IsTransient, parentCorrelationId);

                await RecordAuditAsync(
                    parentCorrelationId,
                    attemptContext,
                    AuditEventType.Retry,
                    AuditSeverity.Info,
                    $"Retry {attempt} scheduled in {delay.TotalMilliseconds:F0}ms for '{operationName}' due to {classification.Category} failure.",
                    outcome: "Scheduled",
                    failureCategory: classification.Category,
                    cancellationToken: cancellationToken);

                // Wait delay before next attempt
                try
                {
                    await _delayProvider.DelayAsync(delay, effectiveToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _metrics.RecordRetryCancelled();
                    await RecordAuditAsync(
                        parentCorrelationId,
                        attemptContext,
                        AuditEventType.Retry,
                        AuditSeverity.Warning,
                        $"Operation '{operationName}' cancelled by caller during retry delay.",
                        outcome: "Cancelled",
                        cancellationToken: CancellationToken.None);

                    throw;
                }
                catch (OperationCanceledException) when (overallCts is not null && overallCts.IsCancellationRequested)
                {
                    _metrics.RecordOperationTimeout();
                    await RecordAuditAsync(
                        parentCorrelationId,
                        attemptContext,
                        AuditEventType.Timeout,
                        AuditSeverity.Error,
                        $"Operation '{operationName}' timed out during retry delay (overall timeout {profile.OperationTimeout}).",
                        outcome: "TimedOut",
                        cancellationToken: CancellationToken.None);

                    throw new OperationTimeoutException(operationName, profile.OperationTimeout!.Value);
                }
            }
        }
    }

    private static bool ShouldRetry(
        int attempt,
        int maxAttempts,
        FailureClassificationResult classification,
        ResilienceProfile profile)
    {
        if (attempt >= maxAttempts)
        {
            return false;
        }

        return IsTransientFailure(classification, profile);
    }

    private static bool IsTransientFailure(
        FailureClassificationResult classification,
        ResilienceProfile profile)
    {
        if (profile.RetryableCategories is not null && profile.RetryableCategories.Contains(classification.Category))
        {
            return true;
        }

        if (profile.RetryTransientFailuresOnly)
        {
            if (!classification.IsTransient)
            {
                return false;
            }

            if (DefaultNonRetryableCategories.Contains(classification.Category))
            {
                return false;
            }

            return true;
        }

        return !DefaultNonRetryableCategories.Contains(classification.Category);
    }

    private bool ShouldTripCircuit(Exception ex, ResilienceProfile profile)
    {
        if (ex is OperationCanceledException || ex is CircuitOpenException || ex is BulkheadRejectedException)
        {
            return false;
        }

        if (ex is RetryExhaustedException retryExhausted)
        {
            return retryExhausted.FailureCategory switch
            {
                FailureCategory.Provider or FailureCategory.Infrastructure or FailureCategory.Timeout => true,
                _ => false
            };
        }

        if (ex is OperationTimeoutException or AttemptTimeoutException)
        {
            return true;
        }

        var classification = _failureClassifier.Classify(ex);
        return classification.Category switch
        {
            FailureCategory.Provider or FailureCategory.Infrastructure or FailureCategory.Timeout => true,
            _ => false
        };
    }

    private async Task RecordAuditAsync(
        Guid correlationId,
        OperationContext? context,
        AuditEventType eventType,
        AuditSeverity severity,
        string message,
        string outcome,
        FailureCategory? failureCategory = null,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _auditLogger.LogAsync(
                executionId: correlationId,
                eventType: eventType,
                severity: severity,
                message: message,
                projectId: context?.ProjectId,
                workItemId: context?.TaskId,
                correlationId: correlationId,
                operationId: context?.OperationId,
                runId: context?.RunId,
                outcome: outcome,
                actorType: "System",
                durationMs: durationMs,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record resilience audit event: {Message}", message);
        }
    }
}
