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

namespace AI.Factory.Api.Tests;

public sealed class RateLimitingTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public RateLimitingTestFactory()
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
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }
            services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connection));

            var secDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(SecurityOptions));
            if (secDescriptor is not null)
            {
                services.Remove(secDescriptor);
            }

            var sec = new SecurityOptions
            {
                RateLimiting = new RateLimitingOptions
                {
                    Enabled = true,
                    General = new RateLimitPolicyOptions { PermitLimit = 2, WindowSeconds = 60, QueueLimit = 0 },
                    Operational = new RateLimitPolicyOptions { PermitLimit = 2, WindowSeconds = 60, QueueLimit = 0 },
                    Expensive = new RateLimitPolicyOptions { PermitLimit = 1, WindowSeconds = 60, QueueLimit = 0 }
                }
            };
            services.AddSingleton(sec);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

public sealed class RateLimitingTests : IClassFixture<RateLimitingTestFactory>
{
    private readonly HttpClient _client;

    public RateLimitingTests(RateLimitingTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ExcessRequests_Return429_WithProblemDetails_AndRetryAfter()
    {
        // First 2 requests within GeneralApi permit limit (2)
        var res1 = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var res2 = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // 3rd request exceeds limit
        var res3 = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);

        // Retry-After header should be present
        Assert.True(res3.Headers.Contains("Retry-After") || res3.Headers.RetryAfter is not null);

        // Correlation ID preserved
        Assert.True(res3.Headers.Contains("X-Correlation-ID"));
        var correlationHeader = res3.Headers.GetValues("X-Correlation-ID").Single();

        // ProblemDetails body
        var body = await res3.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests", body.GetProperty("title").GetString());
        Assert.Equal("too_many_requests", body.GetProperty("code").GetString());
        Assert.Equal(correlationHeader, body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task HealthEndpoints_AreNotBlocked_ByRateLimiting()
    {
        // Health endpoints must remain functional regardless of rate limits
        for (int i = 0; i < 10; i++)
        {
            var res = await _client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }
}
