using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ConfigurationDiagnosticsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public ConfigurationDiagnosticsTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetConfigurationDiagnostics_Returns200_WithSanitizedMetadata()
    {
        var response = await _client.GetAsync("/api/operations/configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("environmentName", out var envProp));
        Assert.False(string.IsNullOrWhiteSpace(envProp.GetString()));

        Assert.True(root.TryGetProperty("isValid", out var isValidProp));
        Assert.True(isValidProp.GetBoolean());

        // Database section
        Assert.True(root.TryGetProperty("database", out var dbProp));
        Assert.True(dbProp.GetProperty("configured").GetBoolean());
        Assert.Equal("Sqlite", dbProp.GetProperty("provider").GetString());
        Assert.True(dbProp.GetProperty("hasConnectionString").GetBoolean());

        // CRITICAL: Ensure raw connection string is NOT exposed in the payload!
        var rawJson = root.GetRawText();
        Assert.DoesNotContain("Data Source=", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("factory.db", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);

        // SecretManagement section
        Assert.True(root.TryGetProperty("secretManagement", out var secMgmtProp));
        Assert.True(secMgmtProp.GetProperty("enabled").GetBoolean());
        Assert.Equal("Environment", secMgmtProp.GetProperty("providerName").GetString());

        // Security section
        Assert.True(root.TryGetProperty("security", out var secProp));
        Assert.True(secProp.TryGetProperty("rateLimitingEnabled", out _));

        // Resilience section
        Assert.True(root.TryGetProperty("resilience", out var resProp));
        Assert.True(resProp.GetProperty("configuredProfilesCount").GetInt32() > 0);
    }
}
