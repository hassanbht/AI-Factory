using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.6 §27 "API" — POST /api/projects/{projectId}/plan/generate HTTP-level coverage.</summary>
public sealed class ProjectPlannerApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public ProjectPlannerApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<Guid> CreatePlanningProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = $"Planner Project {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        foreach (var status in new[] { "Analyzing", "Specification", "Architecture", "Planning" })
        {
            await _client.PatchAsJsonAsync($"/api/projects/{id}/status", new { Status = status });
        }

        return id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await _client.PostAsJsonAsync(
            "/api/agents/definitions",
            new { Name = $"Agent-{agentType}-{Guid.NewGuid():N}", AgentType = agentType, SystemPrompt = "x" });
    }

    [Fact]
    public async Task Generate_ValidProject_CreatesQueuedPlannerAgentRun()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Planner");

        var response = await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
        Assert.False(body.TryGetProperty("workItemId", out var workItemId) && workItemId.ValueKind != JsonValueKind.Null);
        Assert.Equal("Queued", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Generate_ProjectNotInPlanningStatus_Returns409()
    {
        var response1 = await _client.PostAsJsonAsync("/api/projects", new { Name = $"Not Planning {Guid.NewGuid():N}" });
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body1.GetProperty("id").GetGuid();
        await SeedAgentDefinitionAsync("Planner");

        var response = await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Generate_UnknownProject_Returns404()
    {
        var response = await _client.PostAsync($"/api/projects/{Guid.NewGuid()}/plan/generate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_NoPlannerAgentDefinitionRegistered_Returns400()
    {
        var projectId = await CreatePlanningProjectAsync();
        // No "Planner" AgentDefinition seeded.

        var response = await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Generate_CalledTwiceWhileFirstStillQueued_ReturnsSameAgentRun()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Planner");

        var first = await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);
        var second = await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ExistingPlanEndpoint_Behavior_IsUnchangedByGenerateEndpoint()
    {
        // §26 — GET /api/projects/{projectId}/plan must still return null/404
        // for a Project whose Planner AgentRun was only just queued (never
        // executed/materialized in this API-only test host).
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Planner");

        await _client.PostAsync($"/api/projects/{projectId}/plan/generate", content: null);

        var getResponse = await _client.GetAsync($"/api/projects/{projectId}/plan");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
