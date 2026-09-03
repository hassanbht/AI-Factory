using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// Collects and packages safe database diagnostics and operational resilience metrics.
/// Guarantees that connection strings, credentials, and SQL text are never exposed.
/// </summary>
public sealed class DatabaseDiagnosticsService : IDatabaseDiagnosticsService
{
    private readonly FactoryDbContext _db;
    private readonly DatabaseOptions _databaseOptions;
    private readonly DatabaseResilienceOptions _resilienceOptions;
    private readonly IOperationalMetrics _metrics;

    public DatabaseDiagnosticsService(
        FactoryDbContext db,
        DatabaseOptions databaseOptions,
        DatabaseResilienceOptions resilienceOptions,
        IOperationalMetrics metrics)
    {
        _db = db;
        _databaseOptions = databaseOptions;
        _resilienceOptions = resilienceOptions;
        _metrics = metrics;
    }

    public async Task<DatabaseDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        bool isReady;
        try
        {
            isReady = await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            isReady = false;
        }

        var metricsSnapshot = _metrics.GetSnapshot();

        return new DatabaseDiagnosticsSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            ProviderName: _databaseOptions.Provider,
            IsReady: isReady,
            CommandTimeoutSeconds: (int)_resilienceOptions.CommandTimeout.TotalSeconds,
            RetriesEnabled: _resilienceOptions.EnableRetries,
            MaxRetryAttempts: _resilienceOptions.MaxRetryAttempts,
            OptimisticConcurrencyEnabled: _resilienceOptions.EnableOptimisticConcurrency,
            OperationsCount: metricsSnapshot.DatabaseOperations,
            FailuresCount: metricsSnapshot.DatabaseFailures,
            RetriesCount: metricsSnapshot.DatabaseRetries,
            TransientFailuresCount: metricsSnapshot.DatabaseTransientFailures,
            ConcurrencyConflictsCount: metricsSnapshot.DatabaseConcurrencyConflicts,
            BusyFailuresCount: metricsSnapshot.DatabaseBusyFailures,
            LockedFailuresCount: metricsSnapshot.DatabaseLockedFailures,
            TransactionFailuresCount: metricsSnapshot.DatabaseTransactionFailures,
            AverageDurationMs: metricsSnapshot.AverageDatabaseOperationDurationMs ?? 0.0);
    }
}
