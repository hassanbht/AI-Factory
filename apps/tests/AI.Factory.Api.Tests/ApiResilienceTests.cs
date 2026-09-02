using System.Net;
using System.Text.Json;
using AI.Factory.Api.HealthChecks;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ApiResilienceTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;

    public ApiResilienceTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetOperationsResilience_Returns200_WithDependenciesList()
    {
        var response = await _client.GetAsync("/api/operations/resilience");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("dependencies", out var dependencies));
        Assert.Equal(JsonValueKind.Array, dependencies.ValueKind);
    }

    [Fact]
    public async Task CircuitBreakerHealthCheck_Healthy_WhenNoOpenCircuits()
    {
        var registry = new CircuitBreakerRegistry();
        var healthCheck = new CircuitBreakerHealthCheck(registry);

        var context = new HealthCheckContext();
        var result = await healthCheck.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CircuitBreakerHealthCheck_Degraded_WhenCircuitIsOpen()
    {
        var registry = new CircuitBreakerRegistry();
        var cb = registry.GetOrCreate("ExternalAI", new CircuitBreakerProfileOptions
        {
            FailureThreshold = 1
        });
        cb.RecordFailure();

        var healthCheck = new CircuitBreakerHealthCheck(registry);
        var context = new HealthCheckContext();
        var result = await healthCheck.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("ExternalAI", result.Description);
    }

    [Fact]
    public void ApiExceptionMapper_MapsBulkheadRejectedException_To429TooManyRequests()
    {
        var mapper = new ApiExceptionMapper();
        var ex = new BulkheadRejectedException("AgentExecution", maxConcurrentExecutions: 2, maxQueueLength: 5, activeExecutions: 2, queuedExecutions: 5);

        var mapping = mapper.Map(ex);

        Assert.Equal(StatusCodes.Status429TooManyRequests, mapping.StatusCode);
        Assert.Equal("Too many requests", mapping.Title);
        Assert.Equal("too_many_requests", mapping.Code);
        Assert.Equal(FailureCategory.Infrastructure, mapping.FailureCategory);
        Assert.True(mapping.ShouldAudit);
    }
}
