using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// Implements explicit, short-lived transaction boundaries.
/// Guarantees rollback on exception and records transaction failure metrics.
/// External HTTP or agent calls must never be executed within these boundaries.
/// </summary>
public sealed class DatabaseTransactionManager : IDatabaseTransactionManager
{
    private readonly FactoryDbContext _db;
    private readonly IOperationalMetrics _metrics;
    private readonly ILogger<DatabaseTransactionManager> _logger;

    public DatabaseTransactionManager(
        FactoryDbContext db,
        IOperationalMetrics metrics,
        ILogger<DatabaseTransactionManager> logger)
    {
        _db = db;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync<object?>(
            async ct =>
            {
                await operation(ct);
                return null;
            },
            cancellationToken);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // If an ambient transaction is already open, join it without creating a nested transaction
        if (_db.Database.CurrentTransaction != null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.RecordDatabaseTransactionFailure();
            _logger.LogWarning(ex, "Database transaction failed and was rolled back: {ErrorMessage}", ex.Message);

            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Failed to rollback database transaction: {ErrorMessage}", rollbackEx.Message);
            }

            throw;
        }
    }
}
