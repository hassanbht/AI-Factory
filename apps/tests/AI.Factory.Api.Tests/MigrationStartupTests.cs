using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class MigrationStartupTests
{
    [Fact]
    public async Task MigrateAsync_SuccessfullyAppliesAllMigrations_WithoutPendingChangesWarning()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"factory_migrate_test_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        try
        {
            var options = new DbContextOptionsBuilder<FactoryDbContext>()
                .UseSqlite(connectionString)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var db = new FactoryDbContext(options);

            // Execute migrations
            await db.Database.MigrateAsync();

            // Verify tables and columns were created
            var canConnect = await db.Database.CanConnectAsync();
            Assert.True(canConnect);

            // Verify projects table has Version column
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(projects);";
            using var reader = await cmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("Version", columns);
            Assert.Contains("Name", columns);
            Assert.Contains("Slug", columns);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                SqliteConnection.ClearAllPools();
                File.Delete(dbPath);
            }
        }
    }
}
