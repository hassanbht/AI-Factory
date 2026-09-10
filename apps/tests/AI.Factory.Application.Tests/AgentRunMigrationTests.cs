using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Proves the AddAgentDomainModel migration is a safe, data-preserving
/// rename of the legacy "agent_runs" table to "hermes_executions" — not the
/// drop/recreate EF's own diff engine would have produced from a naive
/// CLR-type-name match between the old and new "AgentRun" types.
/// </summary>
public sealed class AgentRunMigrationTests : IDisposable
{
    private const string PreAgentDomainModelBaseline = "20260903091500_AddOptimisticConcurrencyTokens";

    private readonly string _dbPath;

    public AgentRunMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ai-factory-migration-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // SQLite pools connections by connection string, keeping the file
        // handle open after a DbContext is disposed — clear the pool first
        // or the delete below races an open handle.
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private DbContextOptions<FactoryDbContext> Options() =>
        new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

    [Fact]
    public async Task Migrate_ToLatest_FromPreAgentDomainModelBaseline_PreservesLegacyAgentRunAsHermesExecution()
    {
        await using (var db = new FactoryDbContext(Options()))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreAgentDomainModelBaseline);
        }

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var legacyRunId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            await ExecuteAsync(connection,
                """
                INSERT INTO projects (Id, Name, Slug, Status, RootPath, CreatedAtUtc, UpdatedAtUtc, Version)
                VALUES ($id, 'Migration Test Project', 'migration-test-project', 'Implementation', '/tmp/migration-test', $now, $now, 1);
                """,
                ("$id", projectId.ToString()), ("$now", now));

            await ExecuteAsync(connection,
                """
                INSERT INTO work_items (Id, ProjectId, Type, Title, Description, Status, Priority, AttemptCount, MaxAttempts, CreatedAtUtc, UpdatedAtUtc, Version)
                VALUES ($id, $projectId, 'Coding', 'Legacy work item', 'desc', 'Completed', 'Normal', 1, 3, $now, $now, 1);
                """,
                ("$id", workItemId.ToString()), ("$projectId", projectId.ToString()), ("$now", now));

            await ExecuteAsync(connection,
                """
                INSERT INTO agent_runs (Id, WorkItemId, Agent, Model, Provider, Prompt, Response, Success, Error, AttemptNumber, StartedAtUtc, CompletedAtUtc, DurationMs)
                VALUES ($id, $workItemId, 'coder', 'test-model', 'test-provider', 'do it', 'done', 1, NULL, 1, $now, $now, 42);
                """,
                ("$id", legacyRunId.ToString()), ("$workItemId", workItemId.ToString()), ("$now", now));
        }

        await using (var db = new FactoryDbContext(Options()))
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = new FactoryDbContext(Options()))
        {
            // Matched client-side rather than via a SQL WHERE clause: the
            // legacy row's Id was written by a raw ADO.NET INSERT (TEXT
            // affinity), while EF's own Guid parameter binding for a LINQ
            // filter can use a different storage affinity — the row is
            // still there and reads back correctly, but a server-side
            // equality comparison across the two affinities silently
            // matches nothing. Not what this test is trying to prove.
            var all = await db.HermesExecutions.ToListAsync();
            var execution = Assert.Single(all, x => x.Id == legacyRunId);

            Assert.Equal(workItemId, execution.WorkItemId);
            Assert.Equal("coder", execution.Agent);
            Assert.Equal("test-model", execution.Model);
            Assert.Equal("test-provider", execution.Provider);
            Assert.Equal("do it", execution.Prompt);
            Assert.Equal("done", execution.Response);
            Assert.True(execution.Success);
            Assert.Null(execution.Error);
            Assert.Equal(1, execution.AttemptNumber);
            Assert.Equal(42, execution.DurationMs);

            // The old "agent_runs" name must now hold only the new canonical
            // schema — the legacy row must not still be reachable under it.
            var legacyRowStillUnderOldName = await db.AgentRuns.AnyAsync(x => x.Id == legacyRunId);
            Assert.False(legacyRowStillUnderOldName);
        }
    }

    [Fact]
    public async Task Migrate_ToLatest_OnCleanDatabase_Succeeds()
    {
        await using var db = new FactoryDbContext(Options());
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260909075451_AddAgentDomainModel", applied);
        Assert.Contains("20260910073606_AddStructuredAgentResult", applied);
    }

    /// <summary>
    /// Phase 18.1.8 §8 — the AddStructuredAgentResult migration must be a pure
    /// additive column add: an AgentRun written before it exists must survive
    /// untouched, with both new columns reading back null, and the row must
    /// remain fully writable afterward.
    /// </summary>
    [Fact]
    public async Task Migrate_AddStructuredAgentResult_PreservesExistingAgentRunData()
    {
        const string PreStructuredResultBaseline = "20260909101215_AddAgentMemory";

        await using (var db = new FactoryDbContext(Options()))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreStructuredResultBaseline);
        }

        var definitionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            await ExecuteAsync(connection,
                """
                INSERT INTO agent_definitions (Id, Name, Description, AgentType, SystemPrompt, Enabled, CreatedAtUtc, UpdatedAtUtc, Version)
                VALUES ($id, 'Coder', '', 'Coding', '', 1, $now, $now, 1);
                """,
                ("$id", definitionId.ToString()), ("$now", now));

            await ExecuteAsync(connection,
                """
                INSERT INTO agent_runs (Id, AgentDefinitionId, Status, Iteration, Attempt, CorrelationId, Input, Output, CreatedAtUtc, UpdatedAtUtc, Version)
                VALUES ($id, $definitionId, 'Succeeded', 0, 1, $correlationId, 'do the thing', 'pre-existing output', $now, $now, 1);
                """,
                ("$id", runId.ToString()), ("$definitionId", definitionId.ToString()),
                ("$correlationId", correlationId.ToString()), ("$now", now));
        }

        await using (var db = new FactoryDbContext(Options()))
        {
            await db.Database.MigrateAsync();
        }

        // A fresh row created entirely through EF (not raw ADO.NET, and not
        // referencing the raw-inserted AgentDefinition — an FK to a
        // TEXT-affinity raw-inserted key hits the same mismatch) proves the
        // new columns are genuinely writable/readable post-migration.
        Guid freshRunId;
        await using (var db = new FactoryDbContext(Options()))
        {
            var freshDefinition = new AI.Factory.Domain.Entities.AgentDefinition { Name = "Coder", AgentType = "Coding" };
            db.AgentDefinitions.Add(freshDefinition);

            var fresh = new AI.Factory.Domain.Entities.AgentRun
            {
                AgentDefinitionId = freshDefinition.Id,
                Input = "fresh run",
                StructuredResult = "{\"version\":1,\"status\":\"Succeeded\"}",
                StructuredResultVersion = 1
            };
            db.AgentRuns.Add(fresh);
            await db.SaveChangesAsync();
            freshRunId = fresh.Id;
        }

        await using (var db = new FactoryDbContext(Options()))
        {
            var preExisting = (await db.AgentRuns.ToListAsync()).Single(x => x.Id == runId);
            Assert.Equal("do the thing", preExisting.Input);
            Assert.Equal("pre-existing output", preExisting.Output);
            Assert.Null(preExisting.StructuredResult);
            Assert.Null(preExisting.StructuredResultVersion);

            var fresh = await db.AgentRuns.SingleAsync(x => x.Id == freshRunId);
            Assert.Equal("{\"version\":1,\"status\":\"Succeeded\"}", fresh.StructuredResult);
            Assert.Equal(1, fresh.StructuredResultVersion);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
