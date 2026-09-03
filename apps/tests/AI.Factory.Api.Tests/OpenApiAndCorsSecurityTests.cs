using System.Net;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class OpenApiDisabledTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public OpenApiDisabledTestFactory()
    {
        Environment.SetEnvironmentVariable("Security__OpenApi__Enabled", "false");
        Environment.SetEnvironmentVariable("Security__Cors__Enabled", "true");
        Environment.SetEnvironmentVariable("Security__Cors__AllowedOrigins__0", "https://trusted-dashboard.example.com");

        _connection.Open();
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<FactoryDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("Security__OpenApi__Enabled", null);
        Environment.SetEnvironmentVariable("Security__Cors__Enabled", null);
        Environment.SetEnvironmentVariable("Security__Cors__AllowedOrigins__0", null);

        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public sealed class OpenApiAndCorsSecurityTests : IClassFixture<OpenApiDisabledTestFactory>
{
    private readonly HttpClient _client;

    public OpenApiAndCorsSecurityTests(OpenApiDisabledTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task OpenApi_WhenExplicitlyDisabled_Returns404()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cors_WithAllowedOrigin_ReturnsAccessControlHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "https://trusted-dashboard.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("https://trusted-dashboard.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Cors_WithDisallowedOrigin_DoesNotReturnAccessControlHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "https://malicious-site.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
