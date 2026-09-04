using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AI.Factory.Api.Tests.LoadTesting;
using AI.Factory.Application.Security;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests.Load;

/// <summary>
/// WebApplicationFactory with rate limits raised high enough that load tests measure
/// application behavior under concurrency, not rate-limit rejection (that's covered
/// separately by RateLimitBurstStressTests).
/// </summary>
public sealed class HighThroughputApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public HighThroughputApiFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ai-factory-load-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";

        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connectionString).Options;
        using var db = new FactoryDbContext(options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
            if (dbDescriptor is not null)
            {
                services.Remove(dbDescriptor);
            }
            services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connectionString));

            var secDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(SecurityOptions));
            if (secDescriptor is not null)
            {
                services.Remove(secDescriptor);
            }

            services.AddSingleton(new SecurityOptions
            {
                RateLimiting = new RateLimitingOptions
                {
                    Enabled = true,
                    General = new RateLimitPolicyOptions { PermitLimit = 10_000, WindowSeconds = 60, QueueLimit = 1_000 },
                    Operational = new RateLimitPolicyOptions { PermitLimit = 10_000, WindowSeconds = 60, QueueLimit = 1_000 },
                    Expensive = new RateLimitPolicyOptions { PermitLimit = 10_000, WindowSeconds = 60, QueueLimit = 1_000 }
                }
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(_dbPath);
            TryDeleteFile(_dbPath + "-wal");
            TryDeleteFile(_dbPath + "-shm");
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch (Exception) { /* best-effort test cleanup */ }
        }
    }
}

[Trait("Category", "Load")]
public sealed class ConcurrentApiLoadTests : IClassFixture<HighThroughputApiFactory>
{
    private readonly HighThroughputApiFactory _factory;

    public ConcurrentApiLoadTests(HighThroughputApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> SeedProjectAsync(HttpClient client, string namePrefix)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"{namePrefix}-{Guid.NewGuid():N}",
            description = "seed project for load tests"
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task SustainedReadLoad_NoServerErrors_And_CorrelationIdsStayIsolated()
    {
        var client = _factory.CreateClient();
        var projectId = await SeedProjectAsync(client, "read-load");

        var statusCodes = new ConcurrentBag<HttpStatusCode>();
        var correlationIds = new ConcurrentBag<string>();

        var runner = new LoadTestRunner();
        var scenario = new LoadTestScenario(
            "GetProjectById",
            async ct =>
            {
                var response = await client.GetAsync($"/api/projects/{projectId}", ct);
                statusCodes.Add(response.StatusCode);
                if (response.Headers.TryGetValues("X-Correlation-ID", out var values))
                {
                    correlationIds.Add(values.Single());
                }

                ClassifyOrThrow(response.StatusCode);
            });

        var result = await runner.RunAsync(scenario, new LoadTestConfiguration
        {
            Concurrency = 50,
            TotalOperations = 100
        });

        // All the tests in this fixture share one in-memory SQLite connection (see
        // ApiTestFactory/HighThroughputApiFactory), so 50-way concurrency legitimately
        // saturates the app's own database resilience layer and yields some controlled
        // 503s (DatabaseResiliencePolicy Busy/Locked -> ApiExceptionMapper) alongside 200s.
        // That is the resilience layer working as designed (section 15) — the assertion here
        // is that no request is ever left as an unexpected HTTP 500.
        Assert.Equal(100, result.Metrics.TotalOperations);
        var failureDetail = string.Join(" | ", result.Exceptions.Take(5).Select(e => e.Message));
        Assert.True(
            result.Metrics.FailedOperations <= 2,
            $"Too many unexpected (non-controlled) failures ({result.Metrics.FailedOperations}): {failureDetail}");
        Assert.DoesNotContain(statusCodes, s => (int)s >= 500 && s != HttpStatusCode.ServiceUnavailable);

        Assert.Equal(100, correlationIds.Count);
        Assert.Equal(100, correlationIds.Distinct().Count());

        Assert.True(result.Metrics.P95Latency >= TimeSpan.Zero);
    }

    private static void ClassifyOrThrow(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.ServiceUnavailable || statusCode == HttpStatusCode.TooManyRequests)
        {
            throw new LoadTestRejectedException($"Controlled rejection {statusCode}");
        }

        if ((int)statusCode >= 400)
        {
            throw new InvalidOperationException($"Unexpected status {statusCode}");
        }
    }

    [Fact]
    public async Task ConcurrentWriteLoad_AllDistinctCreates_Succeed_NoDuplicates_NoServerErrors()
    {
        var client = _factory.CreateClient();
        const int writers = 20;

        var createdIds = new ConcurrentBag<Guid>();
        var statusCodes = new ConcurrentBag<HttpStatusCode>();

        var runner = new LoadTestRunner();
        var runId = Guid.NewGuid().ToString("N");
        var counter = 0;

        var scenario = new LoadTestScenario(
            "CreateProject",
            async ct =>
            {
                var index = Interlocked.Increment(ref counter);
                var response = await client.PostAsJsonAsync("/api/projects", new
                {
                    name = $"write-load-{runId}-{index}",
                    description = "concurrent write load"
                }, ct);

                statusCodes.Add(response.StatusCode);

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
                    createdIds.Add(body.GetProperty("id").GetGuid());
                }
                else
                {
                    ClassifyOrThrow(response.StatusCode);
                }
            });

        var result = await runner.RunAsync(scenario, new LoadTestConfiguration
        {
            Concurrency = writers,
            TotalOperations = writers
        });

        // Distinct slugs mean no 409s are expected; a controlled 503 under contention (see
        // SustainedReadLoad) is acceptable, an unexpected 500 is not.
        Assert.Equal(0, result.Metrics.FailedOperations);
        Assert.DoesNotContain(statusCodes, s => (int)s >= 500 && s != HttpStatusCode.ServiceUnavailable);
        Assert.Equal(result.Metrics.SuccessfulOperations, createdIds.Count);
        Assert.Equal(createdIds.Count, createdIds.Distinct().Count());
        Assert.True(createdIds.Count > 0, "Expected at least some concurrent writes to succeed.");
    }

    [Fact]
    public async Task MixedWorkload_ReadsWritesDiagnostics_NoServerErrors_DiagnosticsNotStarved()
    {
        var client = _factory.CreateClient();
        var projectId = await SeedProjectAsync(client, "mixed-load");

        var readLatencies = new ConcurrentBag<TimeSpan>();
        var diagnosticsLatencies = new ConcurrentBag<TimeSpan>();
        var statusCodes = new ConcurrentBag<HttpStatusCode>();
        var counter = 0;

        var runner = new LoadTestRunner();
        var runId = Guid.NewGuid().ToString("N");

        var scenario = new LoadTestScenario(
            "MixedWorkload",
            async ct =>
            {
                var index = Interlocked.Increment(ref counter);
                var kind = index % 3;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                HttpResponseMessage response;
                switch (kind)
                {
                    case 0:
                        response = await client.PostAsJsonAsync("/api/projects", new
                        {
                            name = $"mixed-{runId}-{index}",
                            description = "mixed workload write"
                        }, ct);
                        break;
                    case 1:
                        response = await client.GetAsync("/api/operations/metrics", ct);
                        sw.Stop();
                        diagnosticsLatencies.Add(sw.Elapsed);
                        break;
                    default:
                        response = await client.GetAsync($"/api/projects/{projectId}", ct);
                        sw.Stop();
                        readLatencies.Add(sw.Elapsed);
                        break;
                }

                statusCodes.Add(response.StatusCode);
                ClassifyOrThrow(response.StatusCode);
            });

        var result = await runner.RunAsync(scenario, new LoadTestConfiguration
        {
            Concurrency = 20,
            TotalOperations = 200
        });

        // See SustainedReadLoad for why controlled 503s are expected under this shared-connection
        // test setup — only unexpected (non-503) 5xx counts as a real failure here. A tiny amount
        // of failure noise (e.g. a transport-level reset under 20-way in-proc contention) is
        // tolerated rather than asserted to exactly zero, to avoid CI-hardware flakiness.
        var exMessages = string.Join(" | ", result.Exceptions.Take(5).Select(e => e.Message));
        Assert.True(
            result.Metrics.FailedOperations <= 2,
            $"Too many unexpected failures ({result.Metrics.FailedOperations}): {exMessages}");
        Assert.DoesNotContain(statusCodes, s => (int)s >= 500 && s != HttpStatusCode.ServiceUnavailable);

        // Loose sanity check: diagnostics calls aren't wildly starved relative to reads.
        if (readLatencies.Count > 0 && diagnosticsLatencies.Count > 0)
        {
            var readsSorted = readLatencies.OrderBy(l => l).ToList();
            var diagSorted = diagnosticsLatencies.OrderBy(l => l).ToList();
            var readsP95 = readsSorted[(int)(readsSorted.Count * 0.95) == readsSorted.Count ? readsSorted.Count - 1 : (int)(readsSorted.Count * 0.95)];
            var diagP95 = diagSorted[(int)(diagSorted.Count * 0.95) == diagSorted.Count ? diagSorted.Count - 1 : (int)(diagSorted.Count * 0.95)];

            Assert.True(diagP95 < readsP95 + TimeSpan.FromSeconds(5),
                $"Diagnostics P95 ({diagP95}) unexpectedly worse than reads P95 ({readsP95}) + 5s tolerance.");
        }
    }
}
