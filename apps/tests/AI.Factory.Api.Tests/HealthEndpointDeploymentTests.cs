using System.Net;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class HealthEndpointDeploymentTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointDeploymentTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LivenessEndpoint_Returns200_IndependentlyOfExternalState()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task ReadinessEndpoint_Returns200_WhenSystemIsReady()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content);
    }
}
