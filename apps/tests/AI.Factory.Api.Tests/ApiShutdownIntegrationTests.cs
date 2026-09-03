using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ApiShutdownIntegrationTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public ApiShutdownIntegrationTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetActiveOperations_Returns200_WithMetadata()
    {
        var response = await _client.GetAsync("/api/operations/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.TryGetProperty("isStopping", out var isStoppingProp));
        Assert.False(isStoppingProp.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("activeCount", out var activeCountProp));
        Assert.True(activeCountProp.GetInt32() >= 1);
        Assert.True(doc.RootElement.TryGetProperty("operations", out var opsProp));
        Assert.Contains(opsProp.EnumerateArray(), op => op.GetProperty("operationName").GetString()!.Contains("HTTP GET /api/operations/active"));
    }

    [Fact]
    public async Task WhenShutdownInitiated_ReadinessIsUnhealthy_LivenessIsHealthy_AndMutationsReturn503()
    {
        using var isolatedFactory = _factory.WithWebHostBuilder(_ => { });
        using var client = isolatedFactory.CreateClient();
        var shutdown = isolatedFactory.Services.GetRequiredService<IApplicationShutdown>();

        // Before shutdown
        var liveBefore = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveBefore.StatusCode);

        // Initiate shutdown
        shutdown.TryBeginShutdown();

        // Liveness remains healthy
        var liveAfter = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveAfter.StatusCode);

        // Readiness becomes unhealthy
        var readyAfter = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyAfter.StatusCode);

        // Mutations return 503
        var mutateResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            Name = "Blocked Project",
            Description = "Should be rejected with 503"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, mutateResponse.StatusCode);
        Assert.Equal("30", mutateResponse.Headers.RetryAfter?.ToString());

        // Diagnostic read remains available
        var activeOpsResponse = await client.GetAsync("/api/operations/active");
        Assert.Equal(HttpStatusCode.OK, activeOpsResponse.StatusCode);

        var doc = await JsonDocument.ParseAsync(await activeOpsResponse.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.GetProperty("isStopping").GetBoolean());
    }
}
