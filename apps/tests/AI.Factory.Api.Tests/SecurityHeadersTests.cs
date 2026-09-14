using System.Net;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class SecurityHeadersTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public SecurityHeadersTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Response_IncludesExpectedSecurityHeaders_OnSuccess()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        Assert.True(response.Headers.Contains("Permissions-Policy"));

        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Fact]
    public async Task Response_IncludesExpectedSecurityHeaders_OnError()
    {
        var response = await _client.GetAsync("/api/projects/00000000-0000-0000-0000-000000000001");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Fact]
    public async Task ContentSecurityPolicy_IsPresentOnApiRoutes()
    {
        var response = await _client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task SecurityHeaders_AreNotDuplicated()
    {
        var response = await _client.GetAsync("/health");

        Assert.Single(response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Single(response.Headers.GetValues("X-Frame-Options"));
        Assert.Single(response.Headers.GetValues("Referrer-Policy"));
    }
}
