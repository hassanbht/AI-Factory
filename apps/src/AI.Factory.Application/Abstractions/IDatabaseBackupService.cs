namespace AI.Factory.Application.Abstractions;

/// <summary>
/// SQLite-safe backup/restore for the operational database. Application defines the contract;
/// Infrastructure owns the SQLite-specific backup mechanism (see AI.Factory.Infrastructure.Persistence.DatabaseBackupService).
/// Restore is deliberately not exposed through any HTTP endpoint — the only caller is the
/// `--restore-database` CLI mode in the API host's composition root, which never starts the web
/// pipeline, so a restore always runs with no concurrent request traffic.
/// </summary>
public interface IDatabaseBackupService
{
    Task<DatabaseBackupResult> CreateBackupAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken cancellationToken = default);

    Task<DatabaseRestoreResult> RestoreAsync(string backupFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe-to-log backup metadata — never a connection string, credential, or full absolute host path.
/// </summary>
public sealed record DatabaseBackupResult(
    string BackupId,
    string FileName,
    DateTime CreatedAtUtc,
    string Provider,
    long SizeBytes,
    bool ValidationPassed,
    int RetentionDeletedCount);

public sealed record DatabaseRestoreResult(
    string RestoredFromFileName,
    DateTime RestoredAtUtc,
    string? PreRestoreSafetyBackupFileName,
    bool ValidationPassed);
