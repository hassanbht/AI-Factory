using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.1.6 — /api/agents/definitions HTTP-level coverage.</summary>
public sealed class AgentDefinitionsApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public AgentDefinitionsApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateDefinition_WithValidRequest_Returns201_WithLocationAndBody()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = $"Coder {Guid.NewGuid():N}",
            Description = "Implements WorkItems",
            AgentType = "Coding",
            SystemPrompt = "You are a coding agent."
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("id").GetGuid() != Guid.Empty);
        Assert.True(body.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task CreateDefinition_WithEmptyName_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = "",
            AgentType = "Coding"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateDefinition_WithEmptyAgentType_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = "Coder",
            AgentType = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDefinition_ThatExists_Returns200()
    {
        var create = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = $"Reviewer {Guid.NewGuid():N}",
            AgentType = "Review"
        });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.GetAsync($"/api/agents/definitions/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task GetDefinition_ThatDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/agents/definitions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDefinition_WithInvalidId_Returns400()
    {
        var response = await _client.GetAsync("/api/agents/definitions/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListDefinitions_IncludesCreatedDefinition()
    {
        var uniqueName = $"Listed {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/agents/definitions", new { Name = uniqueName, AgentType = "Coding" });

        var response = await _client.GetAsync("/api/agents/definitions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(body!, x => x.GetProperty("name").GetString() == uniqueName);
    }

    [Fact]
    public async Task CreateDefinition_ResponseDoesNotExposeSensitiveDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = $"Secure {Guid.NewGuid():N}",
            AgentType = "Coding",
            SystemPrompt = "internal instructions"
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Only the documented projection fields — nothing implementation-specific.
        var propertyNames = body.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "id", "name", "description", "agentType", "enabled", "createdAtUtc", "updatedAtUtc" },
            propertyNames);
    }
}
