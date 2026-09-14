using System.Net;
using System.Net.Http.Json;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 18.11 §12 "API tests" — HTTP-level coverage for the explicit
/// project-approval boundary (POST /api/projects/{id}/approve) and for the
/// generic status endpoint's new inability to ever produce Completed.
/// </summary>
public sealed class ProjectApprovalApiTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public ProjectApprovalApiTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<Guid> SeedProjectAtStatusAsync(ProjectStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = $"Approval API Test {Guid.NewGuid():N}",
            Slug = $"approval-api-test-{Guid.NewGuid():N}",
            RootPath = string.Empty,
            Status = status,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return projectId;
    }

    [Fact]
    public async Task Approve_FromHumanReview_Returns200AndCompletesProject()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/approve", new { Actor = "reviewer@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(ProjectStatus.Reviewing)]
    [InlineData(ProjectStatus.Testing)]
    [InlineData(ProjectStatus.Implementation)]
    [InlineData(ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Cancelled)]
    public async Task Approve_FromInvalidState_Returns409(ProjectStatus status)
    {
        var projectId = await SeedProjectAtStatusAsync(status);

        var response = await _client.PostAsync($"/api/projects/{projectId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Approve_UnknownProject_Returns404()
    {
        var response = await _client.PostAsync($"/api/projects/{Guid.NewGuid()}/approve", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approve_Repeated_IsIdempotent_BothReturn200Completed()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview);

        var first = await _client.PostAsync($"/api/projects/{projectId}/approve", content: null);
        var second = await _client.PostAsync($"/api/projects/{projectId}/approve", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondBody = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Completed", secondBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approve_ConcurrentRequests_BothSucceed_ProjectIsCompleted()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview);

        var results = await Task.WhenAll(
            _client.PostAsync($"/api/projects/{projectId}/approve", content: null),
            _client.PostAsync($"/api/projects/{projectId}/approve", content: null));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var getResponse = await _client.GetAsync($"/api/projects/{projectId}");
        var body = await getResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GenericStatusEndpoint_CannotCompleteProject_FromHumanReview_Returns409()
    {
        var projectId = await SeedProjectAtStatusAsync(ProjectStatus.HumanReview);

        var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/status", new { Status = "Completed" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var getResponse = await _client.GetAsync($"/api/projects/{projectId}");
        var body = await getResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("HumanReview", body.GetProperty("status").GetString());
    }
}
