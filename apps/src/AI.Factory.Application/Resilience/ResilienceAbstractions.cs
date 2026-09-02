using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Resilience;

public interface IResilienceExecutor
{
    Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        ResilienceProfile profile,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        ResilienceProfile profile,
        CancellationToken cancellationToken = default);
}

public enum BackoffType
{
    Constant = 0,
    Exponential = 1
}

public sealed class ResilienceProfile
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of retries AFTER the initial attempt.
    /// MaxRetryAttempts = 3 means attempt 1 + 3 retries = 4 total attempts.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(15);

    public BackoffType BackoffType { get; set; } = BackoffType.Exponential;

    public bool UseJitter { get; set; } = true;

    public TimeSpan? AttemptTimeout { get; set; }

    public TimeSpan? OperationTimeout { get; set; }

    public bool RetryTransientFailuresOnly { get; set; } = true;

    public HashSet<FailureCategory>? RetryableCategories { get; set; }

    public CircuitBreakerProfileOptions? CircuitBreaker { get; set; }

    public BulkheadProfileOptions? Bulkhead { get; set; }

    public ResilienceProfile Clone()
    {
        return new ResilienceProfile
        {
            Name = Name,
            Enabled = Enabled,
            MaxRetryAttempts = MaxRetryAttempts,
            InitialDelay = InitialDelay,
            MaxDelay = MaxDelay,
            BackoffType = BackoffType,
            UseJitter = UseJitter,
            AttemptTimeout = AttemptTimeout,
            OperationTimeout = OperationTimeout,
            RetryTransientFailuresOnly = RetryTransientFailuresOnly,
            RetryableCategories = RetryableCategories is null ? null : new HashSet<FailureCategory>(RetryableCategories),
            CircuitBreaker = CircuitBreaker?.Clone(),
            Bulkhead = Bulkhead?.Clone()
        };
    }
}

public sealed record RetryContext(
    string OperationName,
    int AttemptNumber,
    int MaxAttempts,
    Guid? CorrelationId,
    Guid? OperationId,
    Guid? ProjectId,
    Guid? TaskId,
    Guid? RunId,
    FailureCategory? FailureCategory,
    bool IsTransient,
    string? LastFailureSummary);

public interface IBackoffCalculator
{
    TimeSpan CalculateDelay(int attempt, ResilienceProfile profile);
}

public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class DefaultDelayProvider : IDelayProvider
{
    public static readonly DefaultDelayProvider Instance = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}

public sealed class RetryExhaustedException : Exception
{
    public int AttemptsPerformed { get; }

    public FailureCategory FailureCategory { get; }

    public RetryExhaustedException(
        string operationName,
        int attemptsPerformed,
        FailureCategory failureCategory,
        Exception innerException)
        : base($"Operation '{operationName}' failed after {attemptsPerformed} attempt(s): {innerException.Message}", innerException)
    {
        AttemptsPerformed = attemptsPerformed;
        FailureCategory = failureCategory;
    }
}

public sealed class OperationTimeoutException : TimeoutException
{
    public TimeSpan Timeout { get; }

    public OperationTimeoutException(string operationName, TimeSpan timeout)
        : base($"Operation '{operationName}' exceeded overall operation timeout of {timeout}.")
    {
        Timeout = timeout;
    }
}

public sealed class AttemptTimeoutException : TimeoutException
{
    public int AttemptNumber { get; }

    public TimeSpan Timeout { get; }

    public AttemptTimeoutException(string operationName, int attemptNumber, TimeSpan timeout)
        : base($"Attempt {attemptNumber} of operation '{operationName}' exceeded attempt timeout of {timeout}.")
    {
        AttemptNumber = attemptNumber;
        Timeout = timeout;
    }
}
