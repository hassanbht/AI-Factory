using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// SQLite-safe backup/restore. Uses <see cref="SqliteConnection.BackupDatabase"/> — SQLite's native
/// online backup API — instead of copying the database file directly, so a backup is always
/// consistent even against a database that is concurrently being written to.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private const string BackupFilePrefix = "ai-factory-";
    private const string BackupSearchPattern = "ai-factory-*.db";

    private readonly DatabaseOptions _databaseOptions;
    private readonly BackupOptions _backupOptions;
    private readonly IOperationalMetrics _metrics;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<DatabaseBackupService> _logger;

    public DatabaseBackupService(
        DatabaseOptions databaseOptions,
        BackupOptions backupOptions,
        IOperationalMetrics metrics,
        IAuditLogger auditLogger,
        ILogger<DatabaseBackupService> logger)
    {
        _databaseOptions = databaseOptions;
        _backupOptions = backupOptions;
        _metrics = metrics;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<DatabaseBackupResult> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        Directory.CreateDirectory(_backupOptions.BackupDirectory);

        var timestamp = DateTime.UtcNow;
        var backupId = $"{BackupFilePrefix}{timestamp:yyyyMMddTHHmmss}Z-{Guid.NewGuid():N}";
        var fileName = backupId + ".db";
        var tempPath = Path.Combine(_backupOptions.BackupDirectory, backupId + ".tmp");
        var finalPath = Path.Combine(_backupOptions.BackupDirectory, fileName);

        try
        {
            var backupTask = Task.Run(
                () =>
                {
                    using var source = new SqliteConnection(_databaseOptions.ConnectionString);
                    source.Open();
                    using var destination = new SqliteConnection($"Data Source={tempPath};Pooling=False");
                    destination.Open();
                    source.BackupDatabase(destination);
                },
                cancellationToken);

            await backupTask.WaitAsync(_backupOptions.BackupTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            TryDelete(tempPath);
            CleanupSqliteSidecars(tempPath);
            _metrics.RecordBackupFailed();
            _logger.LogError(ex, "Database backup timed out or was cancelled after {TimeoutSeconds}s.", _backupOptions.BackupTimeout.TotalSeconds);
            await AuditAsync(AuditEventType.DatabaseBackupFailed, AuditSeverity.Error, "Database backup timed out or was cancelled.", cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            CleanupSqliteSidecars(tempPath);
            _metrics.RecordBackupFailed();
            _logger.LogError(ex, "Database backup failed while writing the temporary backup file.");
            await AuditAsync(AuditEventType.DatabaseBackupFailed, AuditSeverity.Error, "Database backup failed.", cancellationToken);
            throw;
        }

        // BackupDatabase can leave -wal/-shm sidecars next to the temp file even after the
        // connection that created them is disposed; they aren't needed for a file that will only
        // ever be read afterward, and left alone they'd never match the "*.db" retention glob.
        CleanupSqliteSidecars(tempPath);

        var validationPassed = true;
        if (_backupOptions.ValidateAfterBackup)
        {
            validationPassed = ValidateSqliteFile(tempPath);
            CleanupSqliteSidecars(tempPath);
            if (!validationPassed)
            {
                TryDelete(tempPath);
                _metrics.RecordBackupFailed();
                _logger.LogError("Database backup validation (PRAGMA integrity_check) failed for a freshly created backup; discarding it.");
                await AuditAsync(AuditEventType.DatabaseBackupFailed, AuditSeverity.Error, "Database backup validation failed.", cancellationToken);
                throw new InvalidOperationException("Database backup validation failed; the backup was discarded rather than kept in a possibly-corrupt state.");
            }
        }

        // Defensive: Microsoft.Data.Sqlite pools native connections per connection string even
        // after Dispose, which can hold a Windows file handle open long enough to block the
        // rename below. Pooling=False on the connections above already avoids this in the common
        // case; this is a last-resort guard against any pooled handle from elsewhere in-process.
        SqliteConnection.ClearAllPools();
        File.Move(tempPath, finalPath, overwrite: true);
        var deletedCount = ApplyRetention();
        var sizeBytes = new FileInfo(finalPath).Length;

        _metrics.RecordBackupSucceeded();
        if (_backupOptions.ValidateAfterBackup)
        {
            _metrics.RecordBackupValidated();
        }

        _logger.LogInformation(
            "Database backup created: {FileName} ({SizeBytes} bytes, validated={Validated}); retention removed {DeletedCount} older backup(s).",
            fileName, sizeBytes, validationPassed, deletedCount);
        await AuditAsync(
            AuditEventType.DatabaseBackupSucceeded,
            AuditSeverity.Info,
            $"Database backup '{fileName}' created (validated={validationPassed}, {deletedCount} older backup(s) removed by retention).",
            cancellationToken);

        return new DatabaseBackupResult(backupId, fileName, timestamp, _databaseOptions.Provider, sizeBytes, validationPassed, deletedCount);
    }

    public Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupOptions.BackupDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // Ordered by actual file creation time, not filename — two backups created within the
        // same second (timestamp resolution) would otherwise order by their random GUID suffix.
        IReadOnlyList<string> files = Directory
            .GetFiles(_backupOptions.BackupDirectory, BackupSearchPattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => file.Name)
            .ToList();

        return Task.FromResult(files);
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(string backupFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
        {
            _metrics.RecordRestoreFailed();
            throw new FileNotFoundException("The specified backup file was not found.", backupFilePath);
        }

        var backupFileIsValid = ValidateSqliteFile(backupFilePath);
        CleanupSqliteSidecars(backupFilePath);
        if (!backupFileIsValid)
        {
            _metrics.RecordRestoreFailed();
            await AuditAsync(AuditEventType.DatabaseRestoreFailed, AuditSeverity.Error, "Restore aborted: backup file failed integrity validation.", cancellationToken);
            throw new InvalidOperationException($"Backup file '{Path.GetFileName(backupFilePath)}' failed integrity validation; restore aborted without touching the active database.");
        }

        string? preRestoreBackupFileName = null;
        try
        {
            var preRestore = await CreateBackupAsync(cancellationToken);
            preRestoreBackupFileName = preRestore.FileName;
        }
        catch (Exception ex)
        {
            // A failed safety backup must not silently block an operator-requested restore, but it
            // must be visible — this is exactly the kind of thing that must reach the logs.
            _logger.LogWarning(ex, "Pre-restore safety backup could not be created; proceeding with the restore anyway since an operator explicitly requested it.");
        }

        var activeDbPath = ExtractSqliteFilePath(_databaseOptions.ConnectionString);
        var stagingPath = activeDbPath + ".restoring";

        SqliteConnection.ClearAllPools();
        File.Copy(backupFilePath, stagingPath, overwrite: true);
        File.Move(stagingPath, activeDbPath, overwrite: true);

        _metrics.RecordRestoreSucceeded();
        var restoredFileName = Path.GetFileName(backupFilePath);
        _logger.LogInformation("Database restored from backup '{FileName}'.", restoredFileName);
        await AuditAsync(
            AuditEventType.DatabaseRestoreSucceeded,
            AuditSeverity.Warning,
            $"Database restored from backup '{restoredFileName}'" + (preRestoreBackupFileName is not null ? $" (pre-restore safety backup: '{preRestoreBackupFileName}')." : " (no pre-restore safety backup was created)."),
            cancellationToken);

        return new DatabaseRestoreResult(restoredFileName, DateTime.UtcNow, preRestoreBackupFileName, ValidationPassed: true);
    }

    private void EnsureEnabled()
    {
        if (!_backupOptions.Enabled)
        {
            throw new InvalidOperationException("Database backup is disabled (Backup:Enabled=false). Enable it in configuration before requesting a backup.");
        }
    }

    private int ApplyRetention()
    {
        var files = Directory.GetFiles(_backupOptions.BackupDirectory, BackupSearchPattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ToList();

        var toDelete = files.Skip(_backupOptions.RetentionCount).ToList();
        foreach (var file in toDelete)
        {
            TryDelete(file.FullName);
        }

        return toDelete.Count;
    }

    private static bool ValidateSqliteFile(string path)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = command.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractSqliteFilePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("Database connection string does not specify a file-based SQLite data source; restore is not supported for this configuration.");
        }

        return builder.DataSource;
    }

    /// <summary>
    /// Deletes any SQLite WAL/SHM/rollback-journal sidecar files for the given database file path.
    /// Best-effort — a lingering sidecar next to a backup that is only ever read is harmless, just
    /// untidy, so failures here must never surface as a backup/restore failure.
    /// </summary>
    private static void CleanupSqliteSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = dbPath + suffix;
            try
            {
                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete temporary backup file {FileName}", Path.GetFileName(path));
        }
    }

    private async Task AuditAsync(AuditEventType eventType, AuditSeverity severity, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogger.LogAsync(
                executionId: Guid.NewGuid(),
                eventType: eventType,
                severity: severity,
                message: message,
                outcome: eventType.ToString(),
                actorType: "System",
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Audit persistence failures must never block or mask the backup/restore result itself.
        }
    }
}
