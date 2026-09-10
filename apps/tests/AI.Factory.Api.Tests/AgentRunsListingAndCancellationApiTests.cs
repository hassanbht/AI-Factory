using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 18.1.6 — /api/agents/runs listing/filter/pagination and cancellation
/// coverage. Split from AgentRunsApiTests into its own IClassFixture so each
/// class gets its own ApiTestFactory instance (a fresh rate limiter) —
/// together the two classes' run-creation calls would exceed the real
/// "ExpensiveOperation" PermitLimit (10/60s, from appsettings.json).
/// </summary>
public sealed class AgentRunsListingAndCancellationApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public AgentRunsListingAndCancellationApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<Guid> CreateDefinitionAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = $"Coder {Guid.NewGuid():N}",
            AgentType = "Coding"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task ListRuns_FiltersByAgentDefinitionId()
    {
        var definitionA = await CreateDefinitionAsync();
        var definitionB = await CreateDefinitionAsync();

        await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionA, Input = "a" });
        await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionB, Input = "b" });

        var response = await _client.GetAsync($"/api/agents/runs?agentDefinitionId={definitionA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, x => Assert.Equal(definitionA, x.GetProperty("agentDefinitionId").GetGuid()));
    }

    [Fact]
    public async Task ListRuns_FiltersByStatus()
    {
        var definitionId = await CreateDefinitionAsync();
        await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });

        var response = await _client.GetAsync("/api/agents/runs?status=Queued");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, x => Assert.Equal("Queued", x.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ListRuns_WithInvalidStatus_Returns400()
    {
        var response = await _client.GetAsync("/api/agents/runs?status=NotAStatus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListRuns_WithPageSizeAboveLimit_Returns400()
    {
        var response = await _client.GetAsync("/api/agents/runs?pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListRuns_RespectsPagination()
    {
        var definitionId = await CreateDefinitionAsync();
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = $"run-{i}" });
        }

        var response = await _client.GetAsync($"/api/agents/runs?agentDefinitionId={definitionId}&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task CancelRun_ThatIsQueued_Returns202_AndPersistsAsCancelled()
    {
        var definitionId = await CreateDefinitionAsync();
        var create = await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var cancel = await _client.PostAsync($"/api/agents/runs/{id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var getResponse = await _client.GetAsync($"/api/agents/runs/{id}");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancelRun_ThatIsAlreadyCancelled_Returns409()
    {
        var definitionId = await CreateDefinitionAsync();
        var create = await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        await _client.PostAsync($"/api/agents/runs/{id}/cancel", content: null);
        var secondCancel = await _client.PostAsync($"/api/agents/runs/{id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, secondCancel.StatusCode);
    }

    [Fact]
    public async Task CancelRun_ThatDoesNotExist_Returns404()
    {
        var response = await _client.PostAsync($"/api/agents/runs/{Guid.NewGuid()}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
