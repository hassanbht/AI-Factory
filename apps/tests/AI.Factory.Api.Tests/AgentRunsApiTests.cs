using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>Phase 18.1.6 — /api/agents/runs HTTP-level coverage.</summary>
public sealed class AgentRunsApiTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public AgentRunsApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
        _factory = factory;
    }

    private async Task<Guid> CreateDefinitionAsync(string? name = null)
    {
        var response = await _client.PostAsJsonAsync("/api/agents/definitions", new
        {
            Name = name ?? $"Coder {Guid.NewGuid():N}",
            AgentType = "Coding"
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task CreateRun_WithValidDefinition_Returns201_AndPersistsAsQueued()
    {
        var definitionId = await CreateDefinitionAsync();

        var response = await _client.PostAsJsonAsync("/api/agents/runs", new
        {
            AgentDefinitionId = definitionId,
            Input = "do the thing"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("Queued", body.GetProperty("status").GetString());

        // Integration flow (phase spec §22): POST -> 201 -> persisted Queued -> GET -> Queued.
        var getResponse = await _client.GetAsync($"/api/agents/runs/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", getBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateRun_WithUnknownDefinition_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/runs", new
        {
            AgentDefinitionId = Guid.NewGuid(),
            Input = "x"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateRun_WithEmptyDefinitionId_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/runs", new
        {
            AgentDefinitionId = Guid.Empty,
            Input = "x"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRun_DoesNotWaitForExecution_ResponseIsImmediate()
    {
        var definitionId = await CreateDefinitionAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _client.PostAsJsonAsync(
            "/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        sw.Stop();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // No Worker runs in-process for the API host — this is a sanity bound,
        // not a precise timing assertion (avoids flaky timing-sensitive checks).
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Creation took {sw.ElapsedMilliseconds}ms — looks synchronous.");
    }

    [Fact]
    public async Task GetRun_ThatDoesNotExist_Returns404()
    {
        var response = await _client.GetAsync($"/api/agents/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRun_WithInvalidId_Returns400()
    {
        var response = await _client.GetAsync("/api/agents/runs/not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RunDetail_DoesNotExposeInternalHermesOrSecretFields()
    {
        var definitionId = await CreateDefinitionAsync();
        var create = await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.GetAsync($"/api/agents/runs/{id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var propertyNames = body.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("provider", propertyNames);
        Assert.DoesNotContain("model", propertyNames);
        Assert.DoesNotContain("input", propertyNames);
        Assert.DoesNotContain("version", propertyNames);
    }

    [Fact]
    public async Task GetRun_WithPersistedStructuredResult_ReturnsItInResponse()
    {
        var definitionId = await CreateDefinitionAsync();
        var create = await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        // No Worker runs in-process for the API host (Phase 18.1.6 precedent)
        // — write the structured result directly through the same repository
        // the real orchestrator would use, to prove the API's own read/mapping
        // path (not orchestration) end to end.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var run = await repository.GetByIdAsync(id);
            run!.Output = "the answer";
            run.StructuredResult = """{"version":1,"status":"Succeeded","summary":"done","output":"the answer","data":null,"warnings":[],"errors":[],"artifacts":[],"metadata":null}""";
            run.StructuredResultVersion = 1;
            run.TransitionTo(AgentRunStatus.Running);
            run.TransitionTo(AgentRunStatus.Succeeded);
            await repository.UpdateAsync(run);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var reread = await repository.GetByIdAsync(id);
            Assert.NotNull(reread!.StructuredResult);
        }

        var response = await _client.GetAsync($"/api/agents/runs/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var structuredResult = body.GetProperty("structuredResult");

        Assert.Equal(1, structuredResult.GetProperty("version").GetInt32());
        Assert.Equal("Succeeded", structuredResult.GetProperty("status").GetString());
        Assert.Equal("done", structuredResult.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task GetRun_WithoutStructuredResult_ReturnsNullStructuredResult()
    {
        var definitionId = await CreateDefinitionAsync();
        var create = await _client.PostAsJsonAsync("/api/agents/runs", new { AgentDefinitionId = definitionId, Input = "x" });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.GetAsync($"/api/agents/runs/{id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("structuredResult").ValueKind);
    }
}
