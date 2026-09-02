using AI.Factory.Application.Abstractions;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// One IOperationScope per HTTP request — reads X-Correlation-ID if the
/// caller supplied one, otherwise generates one; every _auditLogger.LogAsync
/// call made while handling this request inherits it via the ambient
/// IOperationContext. Registered after GlobalExceptionMiddleware so an
/// unhandled exception still closes the scope as FAILED before the response
/// is written.
/// </summary>
public sealed class CorrelationMiddleware
{
    private const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly IOperationScopeFactory _scopeFactory;

    public CorrelationMiddleware(RequestDelegate next, IOperationScopeFactory scopeFactory)
    {
        _next = next;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var headerValue)
            && Guid.TryParse(headerValue.ToString(), out var parsed)
                ? parsed
                : Guid.NewGuid();

        await using var scope = _scopeFactory.Begin(
            $"HTTP {context.Request.Method} {context.Request.Path}", correlationId: correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId.ToString();
            return Task.CompletedTask;
        });

        try
        {
            await _next(context);

            if (context.Response.StatusCode >= 500)
            {
                scope.Fail($"Response status {context.Response.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }
}
