using System.Net;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DatabaseDiagnosticsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public DatabaseDiagnosticsTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetDatabaseDiagnostics_Returns200_WithSanitizedMetadata()
    {
        var response = await _client.GetAsync("/api/operations/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("providerName", out var providerProp));
        Assert.Equal("Sqlite", providerProp.GetString());

        Assert.True(root.TryGetProperty("isReady", out var isReadyProp));
        Assert.True(isReadyProp.GetBoolean());

        Assert.True(root.TryGetProperty("commandTimeoutSeconds", out var timeoutProp));
        Assert.True(timeoutProp.GetInt32() > 0);

        Assert.True(root.TryGetProperty("retriesEnabled", out var retriesProp));
        Assert.True(retriesProp.GetBoolean());

        Assert.True(root.TryGetProperty("maxRetryAttempts", out var maxAttemptsProp));
        Assert.True(maxAttemptsProp.GetInt32() >= 1);

        Assert.True(root.TryGetProperty("optimisticConcurrencyEnabled", out var optProp));
        Assert.True(optProp.GetBoolean());

        // CRITICAL: Ensure raw connection string, password, and SQL statements are NOT exposed!
        var rawJson = root.GetRawText();
        Assert.DoesNotContain("Data Source=", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("factory.db", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", rawJson, StringComparison.OrdinalIgnoreCase);
    }
}
