using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class RequestValidationTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public RequestValidationTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task GetProject_WithInvalidGuid_Returns400_WithProblemDetails(string id)
    {
        var response = await _client.GetAsync($"/api/projects/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Validation failed", body.GetProperty("title").GetString());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(1, -5)]
    [InlineData(0, -1)]
    public async Task AuditLog_WithNegativePagination_Returns400_WithProblemDetails(int page, int pageSize)
    {
        var response = await _client.GetAsync($"/api/audit-log?page={page}&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuditLog_WithExcessivePageSize_Returns400_WithProblemDetails()
    {
        var response = await _client.GetAsync("/api/audit-log?pageSize=500");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains("PageSize cannot exceed 100", body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task OperationsRecent_WithInvalidCount_Returns400_WithProblemDetails(int count)
    {
        var response = await _client.GetAsync($"/api/operations/recent?count={count}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuditLog_WithInvalidEnumSeverity_Returns400_WithProblemDetails()
    {
        var response = await _client.GetAsync("/api/audit-log?severity=InvalidNonExistentSeverity");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateProject_WithPathTraversalName_Returns400_WithProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            Name = "../../escape-attempt",
            Description = "desc"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains("contains invalid characters", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task StartOrchestrationRun_WithEmptyGuids_Returns400_WithProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/orchestration-runs", new
        {
            ProjectId = Guid.Empty,
            TaskId = Guid.Empty,
            MaxAttempts = 3
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }
}
