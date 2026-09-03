using System.Data.Common;
using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// Provider-aware database resilience policy that classifies database errors
/// (including SQLite Busy/Locked conditions) and executes retry-safe operations with bounded backoff.
/// Avoids nested uncontrolled retries by owning transient database retries while leaving external dependency
/// retries to ResilienceExecutor.
/// </summary>
public sealed class DatabaseResiliencePolicy : IDatabaseResiliencePolicy
{
    private readonly DatabaseResilienceOptions _options;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<DatabaseResiliencePolicy> _logger;

    public DatabaseResiliencePolicy(
        DatabaseResilienceOptions options,
        IOperationalMetrics metrics,
        ILogger<DatabaseResiliencePolicy> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    public bool IsTransient(Exception exception)
    {
        var classification = Classify(exception);
        return classification is DatabaseFailureClassification.Transient
            or DatabaseFailureClassification.Busy
            or DatabaseFailureClassification.Locked
            or DatabaseFailureClassification.Timeout
            or DatabaseFailureClassification.Connection;
    }

    public bool IsConcurrencyConflict(Exception exception)
    {
        return Classify(exception) == DatabaseFailureClassification.Concurrency;
    }

    public DatabaseFailureClassification Classify(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException or ConcurrencyConflictException)
        {
            return DatabaseFailureClassification.Concurrency;
        }

        if (exception is TimeoutException)
        {
            return DatabaseFailureClassification.Timeout;
        }

        if (exception is SqliteException sqliteEx)
        {
            // SQLite error codes:
            // 5 = SQLITE_BUSY (database is locked by another connection)
            // 6 = SQLITE_LOCKED (table/database lock conflict in same connection)
            // 10 = SQLITE_IOERR (transient disk/file I/O error)
            switch (sqliteEx.SqliteErrorCode)
            {
                case 5:
                    return DatabaseFailureClassification.Busy;
                case 6:
                    return DatabaseFailureClassification.Locked;
                case 10:
                    return DatabaseFailureClassification.Transient;
            }

            if (sqliteEx.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            {
                return DatabaseFailureClassification.Busy;
            }

            if (sqliteEx.Message.Contains("locked", StringComparison.OrdinalIgnoreCase))
            {
                return DatabaseFailureClassification.Locked;
            }

            if (sqliteEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return DatabaseFailureClassification.Timeout;
            }
        }

        if (exception is DbException dbEx)
        {
            if (dbEx.IsTransient)
            {
                return DatabaseFailureClassification.Transient;
            }

            if (dbEx.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                dbEx.Message.Contains("network", StringComparison.OrdinalIgnoreCase))
            {
                return DatabaseFailureClassification.Connection;
            }
        }

        return DatabaseFailureClassification.Unknown;
    }

    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;
        var maxAttempts = _options.EnableRetries ? _options.MaxRetryAttempts : 1;

        while (true)
        {
            attempt++;
            try
            {
                var result = await operation(cancellationToken);
                stopwatch.Stop();
                _metrics.RecordDatabaseOperation(true, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var classification = Classify(ex);

                // Concurrency conflicts must never be blindly retried without reloading state
                if (classification == DatabaseFailureClassification.Concurrency)
                {
                    stopwatch.Stop();
                    _metrics.RecordDatabaseOperation(false, stopwatch.ElapsedMilliseconds);
                    _metrics.RecordDatabaseConcurrencyConflict();

                    if (ex is ConcurrencyConflictException)
                    {
                        throw;
                    }

                    throw new ConcurrencyConflictException(
                        "The resource was changed by another operation. Please reload and retry.",
                        innerException: ex);
                }

                var isRetryable = _options.EnableRetries && attempt < maxAttempts && IsTransient(ex);

                if (isRetryable)
                {
                    _metrics.RecordDatabaseRetry();
                    RecordSpecificFailureMetric(classification);

                    var delay = ComputeDelay(attempt);
                    _logger.LogWarning(
                        "Database transient failure on operation '{OperationName}' (Attempt {Attempt}/{MaxAttempts}, Classification={Classification}): {ErrorMessage}. Retrying in {DelayMs}ms",
                        operationName, attempt, maxAttempts, classification, ex.Message, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                stopwatch.Stop();
                _metrics.RecordDatabaseOperation(false, stopwatch.ElapsedMilliseconds);
                RecordSpecificFailureMetric(classification);

                _logger.LogError(
                    ex,
                    "Database operation '{OperationName}' failed permanently or exhausted retries after {Attempt} attempt(s) (Classification={Classification})",
                    operationName, attempt, classification);

                throw;
            }
        }
    }

    public async Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<object?>(
            operationName,
            async ct =>
            {
                await operation(ct);
                return null;
            },
            cancellationToken);
    }

    private void RecordSpecificFailureMetric(DatabaseFailureClassification classification)
    {
        switch (classification)
        {
            case DatabaseFailureClassification.Busy:
                _metrics.RecordDatabaseBusyFailure();
                _metrics.RecordDatabaseTransientFailure();
                break;
            case DatabaseFailureClassification.Locked:
                _metrics.RecordDatabaseLockedFailure();
                _metrics.RecordDatabaseTransientFailure();
                break;
            case DatabaseFailureClassification.Transient:
            case DatabaseFailureClassification.Connection:
            case DatabaseFailureClassification.Timeout:
                _metrics.RecordDatabaseTransientFailure();
                break;
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var exponential = _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, _options.MaxRetryDelay.TotalMilliseconds);
        // Jitter between -20% and +20%
        var jitter = capped * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(10, capped + jitter));
    }
}
