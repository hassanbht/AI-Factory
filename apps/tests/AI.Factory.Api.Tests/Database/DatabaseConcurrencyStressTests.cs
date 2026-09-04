using System.Net;
using System.Net.Http.Json;
using AI.Factory.Api.Tests.Load;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests.Database;

[Trait("Category", "Stress")]
public sealed class DatabaseConcurrencyStressTests
{
    private static DbContextOptions<FactoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(connection).Options;

    // 9.1 — Lost update with more than 2 concurrent writers (shared in-memory connection,
    // same pattern as DatabaseConcurrencyTests, scaled up to 8 writers).
    [Fact]
    public async Task LostUpdate_EightConcurrentWriters_ExactlyOneSucceeds_RestThrowConcurrencyConflict()
    {
        const int writerCount = 8;

        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);

        Guid projectId;
        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
            var project = new Project { Name = "Stress Project", Slug = "stress-project", RootPath = "/stress" };
            await dbInit.Projects.AddAsync(project);
            await dbInit.SaveChangesAsync();
            projectId = project.Id;
            Assert.Equal(1L, project.Version);
        }

        var contexts = Enumerable.Range(0, writerCount).Select(_ => new FactoryDbContext(options)).ToList();
        try
        {
            var entities = new List<Project>();
            foreach (var ctx in contexts)
            {
                entities.Add(await ctx.Projects.SingleAsync(p => p.Id == projectId));
            }

            for (var i = 0; i < writerCount; i++)
            {
                entities[i].Name = $"Updated by writer {i}";
            }

            var tasks = contexts.Select((ctx, i) => SaveAndClassifyAsync(ctx));
            var results = await Task.WhenAll(tasks);

            var succeeded = results.Count(r => r.Succeeded);
            var conflicted = results.Count(r => !r.Succeeded);

            Assert.Equal(1, succeeded);
            Assert.Equal(writerCount - 1, conflicted);
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.IsType<ConcurrencyConflictException>(r.Exception));
        }
        finally
        {
            foreach (var ctx in contexts)
            {
                await ctx.DisposeAsync();
            }
        }

        await using var verify = new FactoryDbContext(options);
        var final = await verify.Projects.SingleAsync(p => p.Id == projectId);
        Assert.Equal(2L, final.Version);
    }

    private static async Task<(bool Succeeded, Exception? Exception)> SaveAndClassifyAsync(FactoryDbContext ctx)
    {
        try
        {
            await ctx.SaveChangesAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    // 9.3 — Real SQLite BUSY contention using a file-backed database (no shared :memory: connection),
    // with a second connection holding an exclusive write transaction while a
    // DatabaseResiliencePolicy-driven write attempts to proceed concurrently.
    //
    // Known limitation: Microsoft.Data.Sqlite's own internal busy-wait (sqlite3_busy_timeout)
    // absorbs the contention by blocking natively inside sqlite3 until the lock frees, regardless
    // of the "Default Timeout" connection-string value observed in this environment — so the
    // contended write here succeeds without DatabaseResiliencePolicy ever seeing a thrown
    // SqliteException to retry. What this test verifies instead is that the contention is REAL:
    // the writer's completion time tracks the holder's hold duration, proving it genuinely waited
    // on the OS/SQLite file lock rather than racing past it. The classification/retry behavior
    // that would fire if a raw SQLITE_BUSY exception were surfaced is verified deterministically,
    // independent of native timing, by Classifier_IdentifiesRawSqliteBusyException_AsBusyClassification below.
    [Fact]
    public async Task RealFileBackedBusyContention_WriterGenuinelyBlocksOnLock_ThenSucceedsAfterRelease()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ai-factory-stress-{Guid.NewGuid():N}.db");
        try
        {
            // Pooling=False: these connections are never returned to Microsoft.Data.Sqlite's
            // process-wide native connection pool, so cleanup only needs to dispose the
            // connections this test itself opened — no global SqliteConnection.ClearAllPools()
            // call, which would risk invalidating unrelated connections other test classes
            // hold open concurrently (xunit runs test classes in parallel by default).
            var baseConnectionString = $"Data Source={tempFile};Pooling=False";
            var holdDuration = TimeSpan.FromMilliseconds(800);

            Guid projectId;
            await using (var initDb = new FactoryDbContext(
                new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(baseConnectionString).Options))
            {
                await initDb.Database.EnsureCreatedAsync();
                var project = new Project { Name = "Busy Test", Slug = "busy-test", RootPath = "/busy" };
                await initDb.Projects.AddAsync(project);
                await initDb.SaveChangesAsync();
                projectId = project.Id;
            }

            using var holderConnection = new SqliteConnection(baseConnectionString);
            await holderConnection.OpenAsync();

            using (var beginCmd = holderConnection.CreateCommand())
            {
                beginCmd.CommandText = "BEGIN IMMEDIATE;";
                await beginCmd.ExecuteNonQueryAsync();
            }

            using (var lockingUpdate = holderConnection.CreateCommand())
            {
                lockingUpdate.CommandText = "UPDATE Projects SET Name = 'locked-by-holder' WHERE Id = @id;";
                lockingUpdate.Parameters.AddWithValue("@id", projectId.ToString());
                await lockingUpdate.ExecuteNonQueryAsync();
            }

            var releaseTask = Task.Run(async () =>
            {
                await Task.Delay(holdDuration);
                using var commitCmd = holderConnection.CreateCommand();
                commitCmd.CommandText = "COMMIT;";
                await commitCmd.ExecuteNonQueryAsync();
            });

            var metrics = new OperationalMetrics();
            var resilienceOptions = new DatabaseResilienceOptions
            {
                EnableRetries = true,
                MaxRetryAttempts = 15,
                InitialRetryDelay = TimeSpan.FromMilliseconds(50),
                MaxRetryDelay = TimeSpan.FromMilliseconds(150)
            };
            var policy = new DatabaseResiliencePolicy(resilienceOptions, metrics, NullLogger<DatabaseResiliencePolicy>.Instance);
            var writerOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(baseConnectionString).Options;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var succeeded = await policy.ExecuteAsync("stress-write", async ct =>
            {
                await using var db = new FactoryDbContext(writerOptions);
                var project = await db.Projects.SingleAsync(p => p.Id == projectId, ct);
                project.Name = "updated-by-contended-writer";
                await db.SaveChangesAsync(ct);
                return true;
            });
            sw.Stop();

            await releaseTask;

            Assert.True(succeeded);

            // Proves real file-level contention occurred: the writer could not have completed
            // before the holder released the lock.
            Assert.True(
                sw.Elapsed >= holdDuration - TimeSpan.FromMilliseconds(100),
                $"Expected the contended write to block for roughly the {holdDuration.TotalMilliseconds}ms hold duration; actual elapsed={sw.ElapsedMilliseconds}ms.");

            await using var verify = new FactoryDbContext(writerOptions);
            var final = await verify.Projects.SingleAsync(p => p.Id == projectId);
            Assert.Equal("updated-by-contended-writer", final.Name);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch (IOException) { /* best-effort cleanup */ }
            }
        }
    }

    // Fallback / deterministic companion to the real-contention test above: verifies the
    // classifier itself correctly identifies a raw SQLITE_BUSY exception, independent of timing.
    [Fact]
    public void Classifier_IdentifiesRawSqliteBusyException_AsBusyClassification()
    {
        var metrics = new OperationalMetrics();
        var policy = new DatabaseResiliencePolicy(
            new DatabaseResilienceOptions(),
            metrics,
            NullLogger<DatabaseResiliencePolicy>.Instance);

        var busyException = new SqliteException("database is locked", 5);

        Assert.Equal(DatabaseFailureClassification.Busy, policy.Classify(busyException));
        Assert.True(policy.IsTransient(busyException));
    }

    // 9.4 — Idempotency under concurrency: ProjectService checks slug uniqueness before insert
    // (see ProjectService.CreateAsync) and Project.Slug also carries a unique DB index as a
    // backstop, so concurrent duplicate-name creates race on both layers. Verify exactly one
    // create succeeds and the database never ends up with two rows for the same slug.
    [Fact]
    public async Task ConcurrentDuplicateSlugCreates_ExactlyOneSucceeds_NoDuplicateRowsPersisted()
    {
        await using var factory = new HighThroughputApiFactory();
        var client = factory.CreateClient();

        const int attempts = 5;
        var sharedName = $"duplicate-slug-{Guid.NewGuid():N}";

        var tasks = Enumerable.Range(0, attempts)
            .Select(_ => client.PostAsJsonAsync("/api/projects", new { name = sharedName, description = "idempotency stress" }));
        var responses = await Task.WhenAll(tasks);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();

        Assert.Equal(1, created);
        Assert.Equal(attempts - 1, rejected.Count);

        foreach (var r in rejected)
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.Conflict, $"Expected 409 Conflict but got {r.StatusCode}. Response: {body}");
        }
    }
}
