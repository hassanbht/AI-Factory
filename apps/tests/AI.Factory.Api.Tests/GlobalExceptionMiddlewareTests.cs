using System.Text.Json;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Exceptions;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Exercises GlobalExceptionMiddleware directly (fake HttpContext, no host)
/// for the exception→ProblemDetails mapping, correlation preservation, and safe redaction.
/// </summary>
public sealed class GlobalExceptionMiddlewareTests
{
    private static async Task<(int Status, JsonElement Body, IHeaderDictionary Headers)> InvokeAsync(
        Exception thrown,
        Action<HttpContext>? configureContext = null)
    {
        var middleware = new GlobalExceptionMiddleware(
            _ => throw thrown,
            NullLogger<GlobalExceptionMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        configureContext?.Invoke(context);

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();

        return (context.Response.StatusCode, body, context.Response.Headers);
    }

    [Fact]
    public async Task ValidationException_MapsTo400_ProblemDetails()
    {
        var (status, body, headers) = await InvokeAsync(new ValidationException("bad input"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("Validation failed", body.GetProperty("title").GetString());
        Assert.Equal("bad input", body.GetProperty("detail").GetString());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        Assert.Equal("https://httpstatuses.com/400", body.GetProperty("type").GetString());

        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(correlationId, headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task NotFoundException_MapsTo404_ProblemDetails()
    {
        var (status, body, headers) = await InvokeAsync(new NotFoundException("missing resource"));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("Resource not found", body.GetProperty("title").GetString());
        Assert.Equal("missing resource", body.GetProperty("detail").GetString());
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal("https://httpstatuses.com/404", body.GetProperty("type").GetString());
        Assert.Equal(body.GetProperty("correlationId").GetString(), headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task ConflictException_MapsTo409_ProblemDetails()
    {
        var (status, body, headers) = await InvokeAsync(new ConflictException("already exists"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("Operation conflict", body.GetProperty("title").GetString());
        Assert.Equal("already exists", body.GetProperty("detail").GetString());
        Assert.Equal("conflict", body.GetProperty("code").GetString());
        Assert.Equal(body.GetProperty("correlationId").GetString(), headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task ForbiddenException_MapsTo403_ProblemDetails()
    {
        var (status, body, _) = await InvokeAsync(new ForbiddenException("nope"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("Forbidden", body.GetProperty("title").GetString());
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TimeoutException_MapsTo504_OperationTimedOut()
    {
        var (status, body, _) = await InvokeAsync(new TimeoutException("timed out"));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, status);
        Assert.Equal("Operation timed out", body.GetProperty("title").GetString());
        Assert.Equal("The operation timed out.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CircuitOpenException_MapsTo503_ServiceUnavailable()
    {
        var (status, body, _) = await InvokeAsync(new CircuitOpenException("Hermes"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("Service unavailable", body.GetProperty("title").GetString());
        Assert.Equal("The service or an external dependency is temporarily unavailable.", body.GetProperty("detail").GetString());
        Assert.Equal("service_unavailable", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnexpectedException_MapsTo500_AndHidesInternalDetails()
    {
        var (status, body, _) = await InvokeAsync(new InvalidOperationException("connection string: Server=secret;Password=123;"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("Internal server error", body.GetProperty("title").GetString());
        Assert.Equal("internal_error", body.GetProperty("code").GetString());

        var detail = body.GetProperty("detail").GetString();
        Assert.Equal("An unexpected error occurred.", detail);
        Assert.DoesNotContain("secret", detail);
        Assert.DoesNotContain("Password", detail);
    }

    [Fact]
    public async Task CustomCorrelationId_IsPreservedInProblemDetails_AndHeader()
    {
        var (status, body, headers) = await InvokeAsync(
            new ValidationException("invalid input"),
            ctx =>
            {
                ctx.Request.Headers["X-Correlation-ID"] = "test-correlation-123";
            });

        Assert.Equal("test-correlation-123", body.GetProperty("correlationId").GetString());
        Assert.Equal("test-correlation-123", headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task EveryResponse_IncludesBothCorrelationId_AndTraceId()
    {
        var (_, body, headers) = await InvokeAsync(new NotFoundException("missing"));

        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(correlationId, headers["X-Correlation-ID"].ToString());
    }
}
