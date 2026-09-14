using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Real HTTP round-trip through the whole pipeline (GlobalExceptionMiddleware
/// included) — proves the middleware is actually wired in Program.cs, not
/// just correct in isolation (covered separately by GlobalExceptionMiddlewareTests).
/// </summary>
public sealed class ApiIntegrationTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(Guid ProjectId, Guid TaskId)> SeedProjectAndTaskAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = $"Orchestration Test {Guid.NewGuid():N}",
            Slug = $"orchestration-test-{Guid.NewGuid():N}",
            RootPath = string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        var taskId = Guid.NewGuid();
        db.WorkItems.Add(new WorkItem
        {
            Id = taskId,
            ProjectId = projectId,
            Title = "Do the thing",
            Status = WorkItemStatus.Draft.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        return (projectId, taskId);
    }

    [Fact]
    public async Task CreateProject_WithEmptyName_Returns400_WithProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = "", Description = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Validation failed", body.GetProperty("title").GetString());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());

        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task CreateProject_Twice_WithSameName_Returns409_WithProblemDetails()
    {
        var name = $"Duplicate Project {Guid.NewGuid():N}";

        var first = await _client.PostAsJsonAsync("/api/projects", new { Name = name, Description = "x" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/projects", new { Name = name, Description = "x" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Operation conflict", body.GetProperty("title").GetString());
        Assert.Equal("conflict", body.GetProperty("code").GetString());
        Assert.Equal(409, body.GetProperty("status").GetInt32());

        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.Equal(correlationId, second.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task HealthLive_ReturnsHealthy_WithoutRunningAnyChecks()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReturnsHealthy_WhenDatabaseAndConfigurationAreValid()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostOrchestrationRun_Returns202Immediately_WithRunIdAndStatusUrl()
    {
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var response = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("runId").GetGuid());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("runId").GetGuid().ToString(), body.GetProperty("statusUrl").GetString());
    }

    [Fact]
    public async Task GetOrchestrationRun_ReturnsRunMetadata()
    {
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var created = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdBody.GetProperty("runId").GetGuid();

        var response = await _client.GetAsync($"/api/orchestration-runs/{runId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(runId, body.GetProperty("id").GetGuid());
        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
        Assert.Equal(taskId, body.GetProperty("workItemId").GetGuid());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.True(body.TryGetProperty("attempt", out _));
        Assert.True(body.TryGetProperty("requestedAtUtc", out _));
    }

    [Fact]
    public async Task GetOrchestrationRun_Returns404_ForUnknownRun()
    {
        var response = await _client.GetAsync($"/api/orchestration-runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostOrchestrationRunCancel_MarksCancellationRequested()
    {
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var created = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdBody.GetProperty("runId").GetGuid();

        var cancelResponse = await _client.PostAsync($"/api/orchestration-runs/{runId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == runId);

        Assert.NotNull(run.CancellationRequestedAtUtc);
    }

    [Fact]
    public async Task PostOrchestrationRunCancel_TransitionsWorkItemToCancelled()
    {
        // Phase 18.13 — cancellation's single authority is the WorkItem, not
        // a second OrchestrationRun-only status field.
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var created = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdBody.GetProperty("runId").GetGuid();

        var cancelResponse = await _client.PostAsync($"/api/orchestration-runs/{runId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);

        // Repeated cancellation must be idempotent, not an error.
        var secondCancelResponse = await _client.PostAsync($"/api/orchestration-runs/{runId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Accepted, secondCancelResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var workItem = await db.WorkItems.SingleAsync(x => x.Id == taskId);

        Assert.Equal(WorkItemStatus.Cancelled.ToString(), workItem.Status);
    }

    [Fact]
    public async Task PostOrchestrationRunCancel_Returns409_AndDoesNotResurrectWorkItem_WhenAgentRunAlreadyCompletedIt()
    {
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var created = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdBody.GetProperty("runId").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            var definition = new AgentDefinition { Name = "Impl", AgentType = "Implementation", SystemPrompt = "x" };
            db.AgentDefinitions.Add(definition);

            var agentRun = new AgentRun
            {
                Id = Guid.NewGuid(),
                AgentDefinitionId = definition.Id,
                WorkItemId = taskId,
                ProjectId = projectId,
                Status = AgentRunStatus.Succeeded
            };
            db.AgentRuns.Add(agentRun);

            var run = await db.OrchestrationRuns.SingleAsync(x => x.Id == runId);
            run.AgentRunId = agentRun.Id;

            var workItem = await db.WorkItems.SingleAsync(x => x.Id == taskId);
            workItem.Status = WorkItemStatus.Completed.ToString();

            await db.SaveChangesAsync();
        }

        var cancelResponse = await _client.PostAsync($"/api/orchestration-runs/{runId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Conflict, cancelResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var untouched = await verifyDb.WorkItems.SingleAsync(x => x.Id == taskId);

        Assert.Equal(WorkItemStatus.Completed.ToString(), untouched.Status);
    }

    [Fact]
    public async Task GetOrchestrationRun_ReturnsOrderedTrace_WithFailureAndMetadata()
    {
        var (projectId, taskId) = await SeedProjectAndTaskAsync();

        var created = await _client.PostAsJsonAsync(
            "/api/orchestration-runs", new { ProjectId = projectId, TaskId = taskId, MaxAttempts = 3 });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdBody.GetProperty("runId").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            db.OrchestrationRunSteps.AddRange(
                new OrchestrationRunStep
                {
                    OrchestrationRunId = runId,
                    Stage = OrchestrationStepKind.WorkspacePreparation,
                    Status = OrchestrationStepStatus.Succeeded,
                    Sequence = 1,
                    StartedAtUtc = DateTime.UtcNow.AddSeconds(-10),
                    CompletedAtUtc = DateTime.UtcNow.AddSeconds(-9)
                },
                new OrchestrationRunStep
                {
                    OrchestrationRunId = runId,
                    Stage = OrchestrationStepKind.Build,
                    Status = OrchestrationStepStatus.Failed,
                    Sequence = 2,
                    AttemptNumber = 1,
                    StartedAtUtc = DateTime.UtcNow.AddSeconds(-8),
                    CompletedAtUtc = DateTime.UtcNow.AddSeconds(-7),
                    FailureType = "BuildFailure",
                    FailureMessage = "compile error",
                    MetadataJson = "{\"model\":\"test-model\"}"
                });

            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/orchestration-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var trace = body.GetProperty("trace").EnumerateArray().ToList();

        Assert.Equal(2, trace.Count);
        Assert.Equal("WorkspacePreparation", trace[0].GetProperty("stage").GetString());
        Assert.Equal("Succeeded", trace[0].GetProperty("status").GetString());
        Assert.Equal("Build", trace[1].GetProperty("stage").GetString());
        Assert.Equal("Failed", trace[1].GetProperty("status").GetString());
        Assert.Equal("BuildFailure", trace[1].GetProperty("failureType").GetString());
        Assert.Equal("test-model", trace[1].GetProperty("metadata").GetProperty("model").GetString());
    }

    [Fact]
    public async Task Request_WithoutCorrelationHeader_GetsOneGeneratedAndReturnedInResponse()
    {
        var response = await _client.GetAsync("/health");

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.True(Guid.TryParse(values!.Single(), out _));
    }

    [Fact]
    public async Task Request_WithCorrelationHeader_EchoesTheSameIdBack()
    {
        var correlationId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-ID", correlationId.ToString());

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(correlationId.ToString(), values!.Single());
    }

    [Fact]
    public async Task GetOperationsMetrics_ReturnsAValidSnapshot()
    {
        var response = await _client.GetAsync("/api/operations/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("operationsStarted", out _));
        Assert.True(body.TryGetProperty("generatedAtUtc", out _));
    }

    [Fact]
    public async Task GetProjectDiagnostics_Returns404_ForUnknownProject()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}/diagnostics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCorrelationSummary_Returns404_WhenNothingWasRecordedForThatCorrelation()
    {
        var response = await _client.GetAsync($"/api/operations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAuditLog_ReturnsPaginatedEnvelope()
    {
        var response = await _client.GetAsync("/api/audit-log?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("items", out _));
        Assert.True(body.TryGetProperty("totalCount", out _));
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("pageSize").GetInt32());
    }

    /// <summary>
    /// Phase 18.12 §10/§13 — the legacy /api/approvals/* endpoints (backed by
    /// ApprovalService/IWorkflowEngine, a second independent Project-transition
    /// authority) were retired in favor of the sole authoritative boundary,
    /// POST /api/projects/{id}/approve (Phase 18.11). The three tests that
    /// previously exercised /api/approvals/{id}/approve|reject were removed
    /// along with their SeedPendingApprovalAsync helper; this test proves the
    /// retirement instead of merely documenting it.
    /// </summary>
    [Fact]
    public async Task LegacyApprovalsEndpoint_NoLongerExists_Returns404()
    {
        var response = await _client.PostAsync(
            $"/api/approvals/{Guid.NewGuid()}/approve", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartOrchestrationRun_WithUnknownProject_Returns404_WithProblemDetails()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/orchestration-runs",
            new { ProjectId = Guid.NewGuid(), TaskId = Guid.NewGuid(), MaxAttempts = 3 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Resource not found", body.GetProperty("title").GetString());
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(404, body.GetProperty("status").GetInt32());

        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task ErrorResponse_PreservesCustomCorrelationHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
        {
            Content = JsonContent.Create(new { Name = "", Description = "x" })
        };
        request.Headers.Add("X-Correlation-ID", "test-correlation-123");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("test-correlation-123", response.Headers.GetValues("X-Correlation-ID").Single());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("test-correlation-123", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ErrorResponse_GeneratesMatchingCorrelationId_WhenNoneSupplied()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = "", Description = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var headerVals));

        var headerCorrelation = headerVals!.Single();
        Assert.True(Guid.TryParse(headerCorrelation, out _));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(headerCorrelation, body.GetProperty("correlationId").GetString());
    }
}
