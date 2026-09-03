namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Diagnostic report for database operations, health, and resilience.
/// Completely avoids exposing connection strings, usernames, passwords, or raw SQL statements.
/// </summary>
public sealed record DatabaseDiagnosticsSnapshot(
    DateTime GeneratedAtUtc,
    string ProviderName,
    bool IsReady,
    int CommandTimeoutSeconds,
    bool RetriesEnabled,
    int MaxRetryAttempts,
    bool OptimisticConcurrencyEnabled,
    long OperationsCount,
    long FailuresCount,
    long RetriesCount,
    long TransientFailuresCount,
    long ConcurrencyConflictsCount,
    long BusyFailuresCount,
    long LockedFailuresCount,
    long TransactionFailuresCount,
    double AverageDurationMs);

/// <summary>
/// Assembles operationally useful database resilience and health diagnostics safely.
/// </summary>
public interface IDatabaseDiagnosticsService
{
    Task<DatabaseDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
