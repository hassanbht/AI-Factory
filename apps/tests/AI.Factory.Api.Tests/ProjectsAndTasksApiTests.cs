using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.2 §12/§15 — Projects, Tasks, and Task-dependency HTTP-level coverage.</summary>
public sealed class ProjectsAndTasksApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public ProjectsAndTasksApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Phase 18.16 §12 — WorkItem creation now requires a Project's source
    /// decision to already be made (Status != Created). Every caller of this
    /// helper that goes on to create a Task defaults straight past that gate
    /// with the simplest deterministic decision (Empty); the few
    /// status-transition tests that specifically need to observe the raw
    /// just-created "Created" state pass initializeSource: false.
    /// </summary>
    private async Task<Guid> CreateProjectAsync(string? name = null, bool initializeSource = true)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = name ?? $"Project {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        if (initializeSource)
        {
            var sourceResponse = await _client.PostAsJsonAsync(
                $"/api/projects/{id}/source", new { SourceType = "Empty" });
            sourceResponse.EnsureSuccessStatusCode();
        }

        return id;
    }

    private async Task<Guid> CreateTaskAsync(Guid projectId, string? title = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/tasks",
            new { Type = "Implementation", Title = title ?? $"Task {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task CreateProject_ValidRequest_Returns201()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = $"P {Guid.NewGuid():N}" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Created", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetProject_ThatExists_Returns200()
    {
        var id = await CreateProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetProject_ThatDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListProjects_IncludesCreatedProject()
    {
        var uniqueName = $"Listed {Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/api/projects", new { Name = uniqueName });

        var response = await _client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(body!, x => x.GetProperty("name").GetString() == uniqueName);
    }

    [Fact]
    public async Task UpdateProjectStatus_ValidTransition_Returns200()
    {
        var id = await CreateProjectAsync(initializeSource: false);

        var response = await _client.PatchAsJsonAsync($"/api/projects/{id}/status", new { Status = "Analyzing" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Analyzing", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateProjectStatus_InvalidTransition_Returns409()
    {
        var id = await CreateProjectAsync(initializeSource: false);

        var response = await _client.PatchAsJsonAsync($"/api/projects/{id}/status", new { Status = "Completed" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProjectStatus_UnknownStatusName_Returns400()
    {
        var id = await CreateProjectAsync(initializeSource: false);

        var response = await _client.PatchAsJsonAsync($"/api/projects/{id}/status", new { Status = "NotAStatus" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateTask_ValidRequest_Returns201()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/tasks", new { Type = "Implementation", Title = "Build the thing" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task CreateTask_UnknownProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{Guid.NewGuid()}/tasks", new { Type = "Implementation", Title = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTask_InvalidType_Returns400()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/tasks", new { Type = "NotAType", Title = "X" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListTasksByProject_ReturnsCreatedTask()
    {
        var projectId = await CreateProjectAsync();
        await CreateTaskAsync(projectId, title: "Listed Task");

        var response = await _client.GetAsync($"/api/projects/{projectId}/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(body!, x => x.GetProperty("title").GetString() == "Listed Task");
    }

    [Fact]
    public async Task GetTask_ThatExists_Returns200()
    {
        var projectId = await CreateProjectAsync();
        var taskId = await CreateTaskAsync(projectId);

        var response = await _client.GetAsync($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTask_ThatDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTaskStatus_ValidTransition_Returns200()
    {
        var projectId = await CreateProjectAsync();
        var taskId = await CreateTaskAsync(projectId);

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{taskId}/status", new { Status = "Ready" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTaskStatus_InvalidTransition_Returns409()
    {
        var projectId = await CreateProjectAsync();
        var taskId = await CreateTaskAsync(projectId);

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{taskId}/status", new { Status = "Running" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddDependency_Valid_Returns201()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);
        var taskB = await CreateTaskAsync(projectId);

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskB });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task AddDependency_SelfDependency_Returns400()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskA });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddDependency_Duplicate_Returns409()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);
        var taskB = await CreateTaskAsync(projectId);
        await _client.PostAsJsonAsync($"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskB });

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskB });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddDependency_CircularDependency_Returns400()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);
        var taskB = await CreateTaskAsync(projectId);
        await _client.PostAsJsonAsync($"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskB });

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskB}/dependencies", new { DependsOnTaskId = taskA });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RemoveDependency_Existing_Returns204()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);
        var taskB = await CreateTaskAsync(projectId);
        await _client.PostAsJsonAsync($"/api/tasks/{taskA}/dependencies", new { DependsOnTaskId = taskB });

        var response = await _client.DeleteAsync($"/api/tasks/{taskA}/dependencies/{taskB}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveDependency_Missing_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var taskA = await CreateTaskAsync(projectId);
        var taskB = await CreateTaskAsync(projectId);

        var response = await _client.DeleteAsync($"/api/tasks/{taskA}/dependencies/{taskB}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReadiness_NoDependencies_ReturnsReady()
    {
        var projectId = await CreateProjectAsync();
        var taskId = await CreateTaskAsync(projectId);

        var response = await _client.GetAsync($"/api/tasks/{taskId}/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", body.GetProperty("readiness").GetString());
    }

    [Fact]
    public async Task GetReadiness_UnknownTask_Returns404()
    {
        var response = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}/readiness");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
