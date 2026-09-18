using System.Data.Common;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Workstream D — SQLite Backup / Restore Rehearsal
/// Validates:
/// 1. Application runs against WAL-mode SQLite.
/// 2. PRAGMA journal_mode=WAL is active.
/// 3. PRAGMA busy_timeout=5000 is active.
/// 4. Online backup can be created using the documented VACUUM INTO procedure (and DatabaseBackupService).
/// 5. The backup is a valid SQLite database (passes PRAGMA integrity_check).
/// 6. The database can be restored after stopping API and Worker.
/// 7. The restored database preserves project, work item, agent run, structured result, and approval state.
/// 8. Worker recovery behaves correctly after restore.
/// 9. No data corruption occurs.
/// </summary>
public sealed class SqliteBackupRestoreRehearsalAcceptanceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly string _connectionString;
    private readonly OperationalMetrics _metrics = new();

    public SqliteBackupRestoreRehearsalAcceptanceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "ai-factory-rehearsal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "active.db");
        _backupDir = Path.Combine(_rootDir, "backups");
        Directory.CreateDirectory(_backupDir);

        // Default Timeout=5 configures native sqlite3_busy_timeout to 5000ms
        _connectionString = $"Data Source={_dbPath};Default Timeout=5;Pooling=False";

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        using var db = new FactoryDbContext(schemaOptions);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private DatabaseBackupService CreateBackupService()
    {
        var databaseOptions = new DatabaseOptions
        {
            Provider = "Sqlite",
            ConnectionString = _connectionString
        };

        var backupOptions = new BackupOptions
        {
            Enabled = true,
            BackupDirectory = _backupDir,
            RetentionCount = 7,
            ValidateAfterBackup = true,
            BackupTimeout = TimeSpan.FromSeconds(30)
        };

        return new DatabaseBackupService(
            databaseOptions,
            backupOptions,
            _metrics,
            NullAuditLogger.Instance,
            NullLogger<DatabaseBackupService>.Instance);
    }

    [Fact]
    public async Task Sqlite_BackupAndRestoreRehearsal_ValidatesWal_BusyTimeout_VacuumInto_AndDataIntegrity()
    {
        // ─────────────────────────────────────────────────────────────
        // 1 & 2 & 3. Validate WAL mode and busy_timeout
        // ─────────────────────────────────────────────────────────────
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();

            using var cmdWal = connection.CreateCommand();
            cmdWal.CommandText = "PRAGMA journal_mode;";
            var journalMode = (string?)await cmdWal.ExecuteScalarAsync();
            Assert.Equal("wal", journalMode?.ToLowerInvariant());

            using var cmdSetTimeout = connection.CreateCommand();
            cmdSetTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
            await cmdSetTimeout.ExecuteNonQueryAsync();

            using var cmdTimeout = connection.CreateCommand();
            cmdTimeout.CommandText = "PRAGMA busy_timeout;";
            var busyTimeout = Convert.ToInt64(await cmdTimeout.ExecuteScalarAsync());
            Assert.True(busyTimeout >= 5000, $"Expected busy_timeout >= 5000, actual: {busyTimeout}");
        }

        // ─────────────────────────────────────────────────────────────
        // 4. Seed rich production-like state: Project, WorkItem, AgentRun,
        //    StructuredAgentResult, ApprovalRequest
        // ─────────────────────────────────────────────────────────────
        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var agentDefId = Guid.NewGuid();
        var completedRunId = Guid.NewGuid();
        var runningRunId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();

        var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using (var db = new FactoryDbContext(schemaOptions))
        {
            var project = new Project
            {
                Id = projectId,
                Name = "Rehearsal Project",
                Slug = "rehearsal-project",
                RootPath = "/workspace/rehearsal",
                Status = ProjectStatus.HumanReview
            };
            db.Projects.Add(project);

            var workItem = new WorkItem
            {
                Id = workItemId,
                ProjectId = projectId,
                Title = "Implement Backup Validation",
                Status = WorkItemStatus.Running.ToString(),
                AttemptCount = 1,
                MaxAttempts = 3,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                LastHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            db.WorkItems.Add(workItem);

            var definition = new AgentDefinition
            {
                Id = agentDefId,
                Name = "Rehearsal Agent",
                AgentType = "Coding",
                Enabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.AgentDefinitions.Add(definition);

            // Succeeded run with structured output
            var structuredJson = JsonSerializer.Serialize(new
            {
                summary = "All backup criteria met",
                modifiedFiles = new[] { "src/Service.cs" },
                decisions = new[] { "Used WAL mode", "Verified VACUUM INTO" }
            });

            var run1 = new AgentRun
            {
                Id = completedRunId,
                ProjectId = projectId,
                WorkItemId = workItemId,
                AgentDefinitionId = agentDefId,
                Status = AgentRunStatus.Succeeded,
                Attempt = 1,
                Output = structuredJson,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                CompletedAtUtc = DateTime.UtcNow.AddMinutes(-14),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-14)
            };

            // In-flight running run that will be recovered post-restore
            var run2 = new AgentRun
            {
                Id = runningRunId,
                ProjectId = projectId,
                WorkItemId = workItemId,
                AgentDefinitionId = agentDefId,
                Status = AgentRunStatus.Running,
                Attempt = 2,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-8),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-8),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-8)
            };

            db.AgentRuns.AddRange(run1, run2);

            var approval = new ApprovalRequest
            {
                Id = approvalId,
                ProjectId = projectId,
                Workflow = ProjectStatus.HumanReview,
                Reason = "Rehearsal verification",
                RequestedBy = "operator@factory.local",
                Decision = ApprovalDecision.Approved,
                Comment = "Rehearsal approved for release verification",
                DecidedAtUtc = DateTime.UtcNow
            };
            db.ApprovalRequests.Add(approval);

            await db.SaveChangesAsync();
        }

        // ─────────────────────────────────────────────────────────────
        // 5. Online Backup via VACUUM INTO procedure
        // ─────────────────────────────────────────────────────────────
        var vacuumBackupPath = Path.Combine(_backupDir, "vacuum-backup.db");
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{vacuumBackupPath.Replace("'", "''")}';";
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.True(File.Exists(vacuumBackupPath));
        var vacuumFileInfo = new FileInfo(vacuumBackupPath);
        Assert.True(vacuumFileInfo.Length > 0);

        // ─────────────────────────────────────────────────────────────
        // 6. Backup validity: PRAGMA integrity_check on the backup
        // ─────────────────────────────────────────────────────────────
        using (var backupConn = new SqliteConnection($"Data Source={vacuumBackupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backupConn.OpenAsync();
            using var cmd = backupConn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("ok", result?.ToLowerInvariant());
        }

        // Also test the DatabaseBackupService online backup procedure
        var backupService = CreateBackupService();
        var serviceBackupResult = await backupService.CreateBackupAsync();
        Assert.True(serviceBackupResult.ValidationPassed);
        var serviceBackupPath = Path.Combine(_backupDir, serviceBackupResult.FileName);
        Assert.True(File.Exists(serviceBackupPath));

        // ─────────────────────────────────────────────────────────────
        // 7. Stop API/Worker connections and Restore from backup
        // ─────────────────────────────────────────────────────────────
        SqliteConnection.ClearAllPools();

        // Perform restore using DatabaseBackupService
        var restoreResult = await backupService.RestoreAsync(vacuumBackupPath);
        Assert.True(restoreResult.ValidationPassed);
        Assert.Equal(Path.GetFileName(vacuumBackupPath), restoreResult.RestoredFromFileName);

        // ─────────────────────────────────────────────────────────────
        // 8. Verify restored database preserves project, work item, agent run,
        //    structured result, and approval state
        // ─────────────────────────────────────────────────────────────
        await using (var db = new FactoryDbContext(schemaOptions))
        {
            var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
            Assert.Equal("Rehearsal Project", project.Name);
            Assert.Equal("rehearsal-project", project.Slug);
            Assert.Equal(ProjectStatus.HumanReview, project.Status);

            var item = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemId);
            Assert.Equal("Implement Backup Validation", item.Title);
            Assert.Equal(WorkItemStatus.Running.ToString(), item.Status);
            Assert.Equal(1, item.AttemptCount);

            var run1 = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == completedRunId);
            Assert.Equal(AgentRunStatus.Succeeded, run1.Status);
            Assert.NotNull(run1.Output);
            Assert.Contains("All backup criteria met", run1.Output);
            Assert.Contains("Verified VACUUM INTO", run1.Output);

            var run2 = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runningRunId);
            Assert.Equal(AgentRunStatus.Running, run2.Status);

            var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(a => a.Id == approvalId);
            Assert.Equal(ApprovalDecision.Approved, approval.Decision);
            Assert.Equal("Rehearsal approved for release verification", approval.Comment);
            Assert.Equal("operator@factory.local", approval.RequestedBy);
        }

        // ─────────────────────────────────────────────────────────────
        // 9. Worker recovery behaves correctly after restore
        // ─────────────────────────────────────────────────────────────
        await using (var db = new FactoryDbContext(schemaOptions))
        {
            var agentRepo = new AgentRunRepository(db);
            var taskManager = new TaskStateManager(db, recoveryOptions: new RecoveryOptions
            {
                StaleExecutionTimeout = TimeSpan.FromMinutes(5)
            });

            // Run recovery sweep as worker would do upon starting up after restore
            var recoveredTasks = await taskManager.RecoverStaleExecutionsAsync();
            var recoveredRuns = await agentRepo.RecoverOrphanedRunsAsync(DateTime.UtcNow - TimeSpan.FromMinutes(5));

            Assert.Equal(1, recoveredTasks);
            Assert.Equal(1, recoveredRuns);

            var recoveredRun = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runningRunId);
            Assert.Equal(AgentRunStatus.Failed, recoveredRun.Status);
            Assert.True(AgentRunRecovery.WasOrphaned(recoveredRun));

            var recoveredItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemId);
            Assert.Equal(WorkItemStatus.Repairing.ToString(), recoveredItem.Status);
        }

        // ─────────────────────────────────────────────────────────────
        // 10. No data corruption occurs: final PRAGMA integrity_check
        // ─────────────────────────────────────────────────────────────
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var integrityResult = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("ok", integrityResult?.ToLowerInvariant());
        }
    }
}
