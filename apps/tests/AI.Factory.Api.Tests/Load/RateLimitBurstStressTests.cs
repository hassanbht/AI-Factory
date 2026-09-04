using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Tight rate-limit window (2s) so burst/recovery behavior can be exercised without a
/// long-running test. Mirrors RateLimitingTestFactory but with a short WindowSeconds.
/// </summary>
public sealed class RateLimitBurstStressFactory : WebApplicationFactory<Program>
{
    public const int PermitLimit = 5;
    public const int WindowSeconds = 2;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public RateLimitBurstStressFactory()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
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
            services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connection));

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
                    General = new RateLimitPolicyOptions { PermitLimit = PermitLimit, WindowSeconds = WindowSeconds, QueueLimit = 0 },
                    Operational = new RateLimitPolicyOptions { PermitLimit = PermitLimit, WindowSeconds = WindowSeconds, QueueLimit = 0 },
                    Expensive = new RateLimitPolicyOptions { PermitLimit = 1, WindowSeconds = WindowSeconds, QueueLimit = 0 }
                }
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // See HighThroughputApiFactory.Dispose for why this is defensive.
            try { _connection.Dispose(); } catch (Exception) { /* best-effort test-fixture cleanup */ }
        }
    }
}

[Trait("Category", "Load")]
public sealed class RateLimitBurstStressTests : IClassFixture<RateLimitBurstStressFactory>
{
    private readonly RateLimitBurstStressFactory _factory;

    public RateLimitBurstStressTests(RateLimitBurstStressFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Burst_AboveLimit_OnlyPermitLimitSucceed_RestGet429WithRetryAfterAndProblemDetails()
    {
        var client = _factory.CreateClient();

        var tasks = Enumerable.Range(0, 30).Select(_ => client.GetAsync("/api/approvals/pending"));
        var responses = await Task.WhenAll(tasks);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var throttled = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();

        // Allow +/-1 for window-boundary races.
        Assert.InRange(okCount, 1, RateLimitBurstStressFactory.PermitLimit + 1);
        Assert.Equal(responses.Length - okCount, throttled.Count);
        Assert.NotEmpty(throttled);

        foreach (var response in throttled)
        {
            Assert.True(response.Headers.Contains("Retry-After") || response.Headers.RetryAfter is not null);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(429, body.GetProperty("status").GetInt32());
            Assert.Equal("too_many_requests", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task HealthEndpoint_StaysAvailable_WhileGeneralApiIsThrottled()
    {
        var client = _factory.CreateClient();

        // Exhaust the GeneralApi window.
        var burst = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/api/approvals/pending")));
        Assert.Contains(burst, r => r.StatusCode == HttpStatusCode.TooManyRequests);

        var healthChecks = new ConcurrentBag<HttpStatusCode>();
        var interleaved = Enumerable.Range(0, 6).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var health = await client.GetAsync("/health/live");
                healthChecks.Add(health.StatusCode);
            }
            else
            {
                await client.GetAsync("/api/approvals/pending");
            }
        });
        await Task.WhenAll(interleaved);

        Assert.NotEmpty(healthChecks);
        Assert.All(healthChecks, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Fact]
    public async Task WindowRecovery_AfterWaitingLongerThanWindow_NewRequestSucceeds()
    {
        var client = _factory.CreateClient();

        // Exhaust the window.
        await Task.WhenAll(Enumerable.Range(0, RateLimitBurstStressFactory.PermitLimit + 5)
            .Select(_ => client.GetAsync("/api/approvals/pending")));

        await Task.Delay(TimeSpan.FromSeconds(RateLimitBurstStressFactory.WindowSeconds + 1));

        var afterRecovery = await client.GetAsync("/api/approvals/pending");
        Assert.Equal(HttpStatusCode.OK, afterRecovery.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequests_ExactlyAtLimit_AllSucceed_OneMoreIsThrottled()
    {
        var client = _factory.CreateClient();

        // Fresh window: wait out any residual state from other tests in this fixture.
        await Task.Delay(TimeSpan.FromSeconds(RateLimitBurstStressFactory.WindowSeconds + 1));

        var atLimit = await Task.WhenAll(Enumerable.Range(0, RateLimitBurstStressFactory.PermitLimit)
            .Select(_ => client.GetAsync("/api/approvals/pending")));

        Assert.All(atLimit, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var oneMore = await client.GetAsync("/api/approvals/pending");
        Assert.Equal(HttpStatusCode.TooManyRequests, oneMore.StatusCode);
    }
}
