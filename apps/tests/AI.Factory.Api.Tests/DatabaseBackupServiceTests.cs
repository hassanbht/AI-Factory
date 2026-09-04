using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 17.4.12 — backup/restore, exercised against real isolated temp SQLite files (never
/// production paths). Each test gets its own scratch directory, cleaned up in a finally block.
/// </summary>
public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly OperationalMetrics _metrics = new();

    public DatabaseBackupServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "ai-factory-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "active.db");
        _backupDir = Path.Combine(_rootDir, "backups");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering handle on Windows must not fail the test run.
        }
    }

    private DatabaseBackupService CreateService(int retentionCount = 7, bool validateAfterBackup = true, bool enabled = true)
    {
        var databaseOptions = new DatabaseOptions { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" };
        var backupOptions = new BackupOptions
        {
            Enabled = enabled,
            BackupDirectory = _backupDir,
            RetentionCount = retentionCount,
            ValidateAfterBackup = validateAfterBackup,
            BackupTimeout = TimeSpan.FromSeconds(10),
        };

        return new DatabaseBackupService(databaseOptions, backupOptions, _metrics, NullAuditLogger.Instance, NullLogger<DatabaseBackupService>.Instance);
    }

    private void SeedActiveDatabase(params string[] projectNames)
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new FactoryDbContext(options);
        db.Database.EnsureCreated();

        foreach (var name in projectNames)
        {
            db.Projects.Add(new Project { Name = name, Slug = name.ToLowerInvariant(), RootPath = "/work/" + name, Status = ProjectStatus.Created });
        }

        db.SaveChanges();
    }

    private List<string> ReadProjectNames()
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var db = new FactoryDbContext(options);
        return db.Projects.OrderBy(p => p.Name).Select(p => p.Name).ToList();
    }

    // ---------------------------------------------------------------
    // Backup creation
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateBackupAsync_CreatesValidatedBackupFile_SourceDatabaseUnchanged()
    {
        SeedActiveDatabase("alpha", "beta");
        var service = CreateService();

        var result = await service.CreateBackupAsync();

        Assert.True(result.ValidationPassed);
        Assert.True(result.SizeBytes > 0);
        Assert.Equal("Sqlite", result.Provider);
        Assert.Equal(0, result.RetentionDeletedCount);

        var backupFilePath = Path.Combine(_backupDir, result.FileName);
        Assert.True(File.Exists(backupFilePath));

        // Source database is untouched by the backup operation.
        Assert.Equal(new[] { "alpha", "beta" }, ReadProjectNames());

        Assert.Equal(1, _metrics.GetSnapshot().BackupSucceeded);
        Assert.Equal(1, _metrics.GetSnapshot().BackupValidated);
        Assert.Equal(0, _metrics.GetSnapshot().BackupFailed);
    }

    [Fact]
    public async Task CreateBackupAsync_WhenDisabled_ThrowsWithoutTouchingAnything()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService(enabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync());

        Assert.False(Directory.Exists(_backupDir));
        Assert.Equal(new[] { "alpha" }, ReadProjectNames());
    }

    [Fact]
    public async Task ListBackupsAsync_ReturnsCreatedBackups_NewestFirst()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService();

        var first = await service.CreateBackupAsync();
        await Task.Delay(10); // ensure a distinguishable creation-time ordering
        var second = await service.CreateBackupAsync();

        var listed = await service.ListBackupsAsync();

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.FileName, listed[0]);
        Assert.Equal(first.FileName, listed[1]);
    }

    // ---------------------------------------------------------------
    // Retention
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateBackupAsync_RetentionCleanup_KeepsOnlyNewestConfiguredCount()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService(retentionCount: 3);

        var created = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var result = await service.CreateBackupAsync();
            created.Add(result.FileName);
            await Task.Delay(10); // distinguishable CreationTimeUtc ordering across iterations
        }

        var remaining = Directory.GetFiles(_backupDir, "ai-factory-*.db").Select(Path.GetFileName).ToList();

        Assert.Equal(3, remaining.Count);
        // The 3 most recently created backups (last 3 in `created`) must be the ones kept.
        Assert.Equal(created.Skip(2).OrderBy(f => f), remaining.OrderBy(f => f));
    }

    // ---------------------------------------------------------------
    // Failure safety
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateBackupAsync_AlreadyCancelled_FailsCleanly_NoLeftoverFiles()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateBackupAsync(cts.Token));

        Assert.Equal(0, _metrics.GetSnapshot().BackupSucceeded);
        Assert.Equal(1, _metrics.GetSnapshot().BackupFailed);

        if (Directory.Exists(_backupDir))
        {
            Assert.Empty(Directory.GetFiles(_backupDir));
        }

        // Source database remains fully intact.
        Assert.Equal(new[] { "alpha" }, ReadProjectNames());
    }

    // ---------------------------------------------------------------
    // Restore
    // ---------------------------------------------------------------

    [Fact]
    public async Task Restore_RoundTrip_ReturnsDatabaseToPreviousState()
    {
        SeedActiveDatabase("alpha", "beta");
        var service = CreateService();

        var backup = await service.CreateBackupAsync();
        var backupFilePath = Path.Combine(_backupDir, backup.FileName);

        // Modify the active database after the backup was taken.
        SeedActiveDatabase("gamma-added-after-backup");
        Assert.Equal(new[] { "alpha", "beta", "gamma-added-after-backup" }, ReadProjectNames());

        var restoreResult = await service.RestoreAsync(backupFilePath);

        Assert.Equal(backup.FileName, restoreResult.RestoredFromFileName);
        Assert.True(restoreResult.ValidationPassed);
        Assert.NotNull(restoreResult.PreRestoreSafetyBackupFileName);

        // Database is back to its pre-modification state.
        Assert.Equal(new[] { "alpha", "beta" }, ReadProjectNames());
        Assert.Equal(1, _metrics.GetSnapshot().RestoreSucceeded);
    }

    [Fact]
    public async Task RestoreAsync_CorruptBackupFile_AbortsWithoutTouchingActiveDatabase()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService();

        var corruptBackupPath = Path.Combine(_rootDir, "corrupt.db");
        await File.WriteAllBytesAsync(corruptBackupPath, new byte[] { 1, 2, 3, 4, 5 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(corruptBackupPath));

        Assert.Equal(1, _metrics.GetSnapshot().RestoreFailed);
        Assert.Equal(0, _metrics.GetSnapshot().RestoreSucceeded);

        // Active database is untouched — restore aborted before ever replacing it.
        Assert.Equal(new[] { "alpha" }, ReadProjectNames());
    }

    [Fact]
    public async Task RestoreAsync_MissingBackupFile_ThrowsFileNotFound()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.RestoreAsync(Path.Combine(_rootDir, "does-not-exist.db")));

        Assert.Equal(new[] { "alpha" }, ReadProjectNames());
    }

    [Fact]
    public async Task CreateBackupAsync_ConcurrentInvocation_RejectsWithInvalidOperationException()
    {
        SeedActiveDatabase("alpha");
        var service = CreateService();

        Directory.CreateDirectory(_backupDir);
        var lockFile = Path.Combine(_backupDir, ".backup.lock");

        // Simulate another backup process holding the exclusive lock
        using (var lockStream = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync());
            Assert.Contains("Another database backup operation is currently in progress", ex.Message);
        }

        // Once the lock is released, backup succeeds normally
        var backup = await service.CreateBackupAsync();
        Assert.NotNull(backup);
    }
}
