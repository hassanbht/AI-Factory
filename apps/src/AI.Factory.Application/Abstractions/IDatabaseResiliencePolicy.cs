namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Detailed classification of database-related failures.
/// </summary>
public enum DatabaseFailureClassification
{
    Transient,
    Busy,
    Locked,
    Timeout,
    Connection,
    Concurrency,
    Permanent,
    Unknown
}

/// <summary>
/// Provider-aware policy for classifying database exceptions and executing retry-safe database operations.
/// </summary>
public interface IDatabaseResiliencePolicy
{
    bool IsTransient(Exception exception);

    bool IsConcurrencyConflict(Exception exception);

    DatabaseFailureClassification Classify(Exception exception);

    Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}
