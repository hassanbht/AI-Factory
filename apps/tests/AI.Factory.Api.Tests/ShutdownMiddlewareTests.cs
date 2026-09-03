using System.Text.Json;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Shutdown;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ShutdownMiddlewareTests
{
    private readonly OperationalMetrics _metrics = new();

    [Fact]
    public async Task Request_PassesThrough_WhenNotStopping()
    {
        using var shutdown = new ApplicationShutdownCoordinator();
        var nextInvoked = false;
        RequestDelegate next = ctx =>
        {
            nextInvoked = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        };

        var middleware = new ShutdownMiddleware(next, shutdown, NullLogger<ShutdownMiddleware>.Instance, _metrics);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/projects";

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task Request_Returns503_WhenStopping()
    {
        using var shutdown = new ApplicationShutdownCoordinator();
        shutdown.TryBeginShutdown();

        var nextInvoked = false;
        RequestDelegate next = _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        };

        var middleware = new ShutdownMiddleware(next, shutdown, NullLogger<ShutdownMiddleware>.Instance, _metrics);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/projects";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("30", context.Response.Headers.RetryAfter.ToString());

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(problem);
        Assert.Equal(503, problem.Status);
        Assert.Contains("shutting down", problem.Detail);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/operations/active")]
    public async Task ExemptPaths_PassThrough_WhenStopping(string exemptPath)
    {
        using var shutdown = new ApplicationShutdownCoordinator();
        shutdown.TryBeginShutdown();

        var nextInvoked = false;
        RequestDelegate next = ctx =>
        {
            nextInvoked = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        };

        var middleware = new ShutdownMiddleware(next, shutdown, NullLogger<ShutdownMiddleware>.Instance, _metrics);
        var context = new DefaultHttpContext();
        context.Request.Path = exemptPath;

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(200, context.Response.StatusCode);
    }
}
