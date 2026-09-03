namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Manages short-lived, atomic transaction boundaries for multi-step database operations.
/// Guarantees automatic rollback on failure and supports cancellation.
/// </summary>
public interface IDatabaseTransactionManager
{
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
