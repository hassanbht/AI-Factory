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

public sealed class OperationalSecurityTestFactory : WebApplicationFactory<Program>
{
    public const string ValidKey = "OperationalSecretKey123456789";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public OperationalSecurityTestFactory()
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
                OperationalEndpoints = new OperationalEndpointsOptions
                {
                    RequireAccessKey = true,
                    AccessKey = ValidKey,
                    HeaderName = "X-Operations-Key"
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

public sealed class OperationalEndpointSecurityTests : IClassFixture<OperationalSecurityTestFactory>
{
    private readonly HttpClient _client;

    public OperationalEndpointSecurityTests(OperationalSecurityTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task OperationalEndpoint_WithoutKey_Returns401_WithProblemDetails()
    {
        var response = await _client.GetAsync("/api/operations/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("Unauthorized", body.GetProperty("title").GetString());
        Assert.Equal("unauthorized", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OperationalEndpoint_WithInvalidKey_Returns401_WithProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operations/metrics");
        request.Headers.Add("X-Operations-Key", "WrongKeyInvalid123456789");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OperationalEndpoint_WithValidKey_InOperationsKeyHeader_Returns200()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operations/metrics");
        request.Headers.Add("X-Operations-Key", OperationalSecurityTestFactory.ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OperationalEndpoint_WithValidKey_InApiKeyHeader_Returns200()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operations/metrics");
        request.Headers.Add("X-Api-Key", OperationalSecurityTestFactory.ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OperationalEndpoint_WithValidKey_InBearerAuthorizationHeader_Returns200()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operations/metrics");
        request.Headers.Add("Authorization", $"Bearer {OperationalSecurityTestFactory.ValidKey}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoints_ArePublic_AndDoNotRequireOperationalKey()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BusinessEndpoints_DoNotRequireOperationalKey()
    {
        var response = await _client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
