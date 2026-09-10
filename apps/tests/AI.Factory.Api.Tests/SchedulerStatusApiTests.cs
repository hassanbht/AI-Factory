using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.3 §15 — scheduler status endpoint.</summary>
public sealed class SchedulerStatusApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public SchedulerStatusApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSchedulerStatus_Returns200WithExpectedShape()
    {
        var response = await _client.GetAsync("/api/scheduler/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("enabled", out _));
        Assert.True(body.TryGetProperty("maxConcurrentTasks", out _));
        Assert.True(body.TryGetProperty("totalDispatched", out _));
        Assert.True(body.TryGetProperty("totalFailures", out _));
    }
}
