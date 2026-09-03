using System.Net;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DeploymentDiagnosticsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public DeploymentDiagnosticsTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetDeploymentDiagnostics_Returns200_WithSanitizedMetadata()
    {
        var response = await _client.GetAsync("/api/operations/deployment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("applicationName", out var appProp));
        Assert.False(string.IsNullOrWhiteSpace(appProp.GetString()));

        Assert.True(root.TryGetProperty("environment", out var envProp));
        Assert.False(string.IsNullOrWhiteSpace(envProp.GetString()));

        Assert.True(root.TryGetProperty("version", out var verProp));
        Assert.False(string.IsNullOrWhiteSpace(verProp.GetString()));

        Assert.True(root.TryGetProperty("frameworkDescription", out var frameworkProp));
        Assert.Contains(".NET", frameworkProp.GetString());

        Assert.True(root.TryGetProperty("processArchitecture", out var archProp));
        Assert.False(string.IsNullOrWhiteSpace(archProp.GetString()));

        Assert.True(root.TryGetProperty("databaseProvider", out var dbProp));
        Assert.Equal("Sqlite", dbProp.GetString());

        Assert.True(root.TryGetProperty("isContainerized", out _));
        Assert.True(root.TryGetProperty("uptime", out _));

        // CRITICAL: Deployment diagnostics must NEVER expose connection strings, passwords, or secret values!
        var rawJson = root.GetRawText();
        Assert.DoesNotContain("Data Source=", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("factory.db", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);
    }
}
