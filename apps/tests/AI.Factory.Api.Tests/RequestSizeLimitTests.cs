using System.Net;
using System.Net.Http.Json;
using System.Text;
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

public sealed class SmallBodyLimitTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SmallBodyLimitTestFactory()
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

            var limitSecurity = new SecurityOptions
            {
                RequestLimits = new RequestLimitsOptions
                {
                    MaxRequestBodySizeBytes = 512 // 512 bytes limit for testing
                }
            };
            services.AddSingleton(limitSecurity);
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

public sealed class RequestSizeLimitTests : IClassFixture<SmallBodyLimitTestFactory>
{
    private readonly HttpClient _client;

    public RequestSizeLimitTests(SmallBodyLimitTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Request_ExceedingConfiguredBodyLimit_Returns413_WithProblemDetails()
    {
        // 1000 characters > 512 bytes limit
        var hugePayload = new string('x', 1000);
        var content = new StringContent(
            JsonSerializer.Serialize(new { Name = "test", Description = hugePayload }),
            Encoding.UTF8,
            "application/json");

        var response = await _client.PostAsync("/api/projects", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(413, body.GetProperty("status").GetInt32());
        Assert.Equal("Payload Too Large", body.GetProperty("title").GetString());
        Assert.Equal("payload_too_large", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Request_WithinBodyLimit_IsAcceptedByMiddleware()
    {
        var smallPayload = new { Name = "Valid-Small-Project", Description = "Short description" };
        var response = await _client.PostAsJsonAsync("/api/projects", smallPayload);

        // Created (201) or Conflict (409) both indicate the payload passed the 413 size boundary
        Assert.True(response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict);
    }
}
