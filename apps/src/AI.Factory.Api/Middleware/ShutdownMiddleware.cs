using System.Text.Json;
using AI.Factory.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Intercepts incoming requests during graceful application shutdown. Rejects new requests
/// with HTTP 503 Service Unavailable, while allowing health checks and diagnostics to continue responding.
/// </summary>
public sealed class ShutdownMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly IApplicationShutdown _shutdown;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILogger<ShutdownMiddleware> _logger;

    public ShutdownMiddleware(
        RequestDelegate next,
        IApplicationShutdown shutdown,
        ILogger<ShutdownMiddleware> logger,
        IOperationalMetrics? metrics = null)
    {
        _next = next;
        _shutdown = shutdown;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_shutdown.IsStopping && !IsExemptPath(context.Request.Path))
        {
            _metrics?.RecordShutdownRequestRejected();
            _logger.LogWarning(
                "Request rejected due to application shutdown: {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "30";
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = "https://httpstatuses.com/503",
                Title = "Service Unavailable",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = "The service is shutting down and cannot accept new requests."
            };

            await JsonSerializer.SerializeAsync(context.Response.Body, problem, JsonOptions);
            return;
        }

        await _next(context);
    }

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/operations/active", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/api/diagnostics/operations", StringComparison.OrdinalIgnoreCase);
    }
}
