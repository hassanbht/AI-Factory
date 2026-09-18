using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.5 §16/§17 — planning API HTTP-level coverage.</summary>
public sealed class ProjectPlanApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public ProjectPlanApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<Guid> CreatePlanningProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = $"Plan Project {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        foreach (var status in new[] { "Analyzing", "Specification", "Architecture", "Planning" })
        {
            var patchRes = await _client.PatchAsJsonAsync($"/api/projects/{id}/status", new { Status = status });
            patchRes.EnsureSuccessStatusCode();
        }

        return id;
    }

    private async Task SeedAgentDefinitionAsync(string agentType)
    {
        await _client.PostAsJsonAsync(
            "/api/agents/definitions",
            new { Name = $"Agent-{agentType}-{Guid.NewGuid():N}", AgentType = agentType, SystemPrompt = "x" });
    }

    private static object OneTaskPlan(string key = "a") => new
    {
        Summary = "Test plan",
        Tasks = new[]
        {
            new { PlanKey = key, Title = "Task A", Description = "d", Type = "Implementation", Priority = "Normal", DependsOn = Array.Empty<string>(), AcceptanceCriteria = new { version = 1, criteria = new[] { new { type = "FileExists", path = "README.md" } } } }
        }
    };

    [Fact]
    public async Task CreatePlan_ValidRequest_Returns200WithPersistedTasks()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", OneTaskPlan());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = body.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Single(tasks);
        Assert.Equal("a", tasks[0].GetProperty("planKey").GetString());
        Assert.Equal("Ready", tasks[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreatePlan_ProjectNotInPlanningStatus_Returns409()
    {
        var response1 = await _client.PostAsJsonAsync("/api/projects", new { Name = $"Not Planning {Guid.NewGuid():N}" });
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body1.GetProperty("id").GetGuid();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", OneTaskPlan());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_InvalidWorkItemType_Returns400()
    {
        var projectId = await CreatePlanningProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", new
        {
            Tasks = new[]
            {
                new { PlanKey = "a", Title = "T", Type = "NotAType", Priority = "Normal", DependsOn = Array.Empty<string>(), AcceptanceCriteria = new { version = 1, criteria = new[] { new { type = "FileExists", path = "README.md" } } } }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_CircularDependency_Returns400()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", new
        {
            Tasks = new[]
            {
                new { PlanKey = "a", Title = "A", Type = "Implementation", Priority = "Normal", DependsOn = new[] { "b" }, AcceptanceCriteria = new { version = 1, criteria = new[] { new { type = "FileExists", path = "README.md" } } } },
                new { PlanKey = "b", Title = "B", Type = "Implementation", Priority = "Normal", DependsOn = new[] { "a" }, AcceptanceCriteria = new { version = 1, criteria = new[] { new { type = "FileExists", path = "README.md" } } } }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePlan_CalledTwiceWithSameTasks_Returns200BothTimes_NoDuplication()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");

        var first = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", OneTaskPlan());
        var second = await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", OneTaskPlan());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var getResponse = await _client.GetAsync($"/api/projects/{projectId}/plan");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(body.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task GetPlan_NoPlanYet_Returns404()
    {
        var projectId = await CreatePlanningProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{projectId}/plan");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPlan_AfterCreation_ExposesStableKeyTypePriorityDependenciesStatusReadiness()
    {
        var projectId = await CreatePlanningProjectAsync();
        await SeedAgentDefinitionAsync("Implementation");
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/plan", OneTaskPlan());

        var response = await _client.GetAsync($"/api/projects/{projectId}/plan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var task = body.GetProperty("tasks").EnumerateArray().Single();

        Assert.True(task.TryGetProperty("planKey", out _));
        Assert.True(task.TryGetProperty("type", out _));
        Assert.True(task.TryGetProperty("priority", out _));
        Assert.True(task.TryGetProperty("dependsOn", out _));
        Assert.True(task.TryGetProperty("status", out _));
        Assert.True(task.TryGetProperty("readiness", out _));
        Assert.Equal("Ready", task.GetProperty("readiness").GetString());
    }
}
