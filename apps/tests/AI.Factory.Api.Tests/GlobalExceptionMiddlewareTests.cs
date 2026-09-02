using System.Text.Json;
using AI.Factory.Api.Middleware;
using AI.Factory.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Exercises GlobalExceptionMiddleware directly (fake HttpContext, no host)
/// for the exception→status/envelope mapping — the "unexpected exception"
/// case is deliberately not routed through a real endpoint, since no
/// business endpoint should ever throw an arbitrary Exception on purpose.
/// </summary>
public sealed class GlobalExceptionMiddlewareTests
{
    private static async Task<(int Status, JsonElement Body)> InvokeAsync(Exception thrown)
    {
        var middleware = new GlobalExceptionMiddleware(_ => throw thrown, NullLogger<GlobalExceptionMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();

        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task ValidationException_MapsTo400()
    {
        var (status, body) = await InvokeAsync(new ValidationException("bad input"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("bad input", body.GetProperty("message").GetString());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NotFoundException_MapsTo404()
    {
        var (status, body) = await InvokeAsync(new NotFoundException("missing"));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConflictException_MapsTo409()
    {
        var (status, body) = await InvokeAsync(new ConflictException("already exists"));

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal("conflict", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ForbiddenException_MapsTo403()
    {
        var (status, _) = await InvokeAsync(new ForbiddenException("nope"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task UnauthorizedAccessException_MapsTo403()
    {
        var (status, _) = await InvokeAsync(new UnauthorizedAccessException());

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task TimeoutException_MapsToTimeoutStatus()
    {
        var (status, _) = await InvokeAsync(new TimeoutException());

        Assert.Equal(StatusCodes.Status408RequestTimeout, status);
    }

    [Fact]
    public async Task OperationCanceledException_MapsToTimeoutStatus()
    {
        var (status, _) = await InvokeAsync(new OperationCanceledException());

        Assert.Equal(StatusCodes.Status408RequestTimeout, status);
    }

    [Fact]
    public async Task UnexpectedException_MapsTo500_AndHidesInternalDetails()
    {
        var (status, body) = await InvokeAsync(new InvalidOperationException("connection string: super-secret"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());

        var message = body.GetProperty("message").GetString();
        Assert.Equal("An unexpected error occurred.", message);
        Assert.DoesNotContain("super-secret", message);
    }

    [Fact]
    public async Task EveryResponse_IncludesTraceId()
    {
        var (_, body) = await InvokeAsync(new NotFoundException("missing"));

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
