using System.Text.Json;
using AI.Factory.Application.Exceptions;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Single place exceptions become HTTP responses. Controllers/services never
/// translate exceptions to status codes themselves — they throw the
/// specific Application exception (or let an unexpected one propagate) and
/// this middleware maps it, so client-facing error shape stays consistent
/// and internal details (stack traces, SQL, file paths) never leak.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (status, code) = Classify(exception);

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "Request failed on {Method} {Path}: {Code}", context.Request.Method, context.Request.Path, code);
        }

        var message = status == StatusCodes.Status500InternalServerError
            ? "An unexpected error occurred."
            : exception.Message;

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = status;

        var envelope = new ErrorEnvelope(context.TraceIdentifier, status, code, message);
        await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, EnvelopeOptions));
    }

    private static (int Status, string Code) Classify(Exception exception)
    {
        return exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "validation_error"),
            NotFoundException => (StatusCodes.Status404NotFound, "not_found"),
            ConflictException => (StatusCodes.Status409Conflict, "conflict"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "forbidden"),
            TimeoutException => (StatusCodes.Status408RequestTimeout, "timeout"),
            OperationCanceledException => (StatusCodes.Status408RequestTimeout, "timeout"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error")
        };
    }

    private sealed record ErrorEnvelope(string TraceId, int Status, string Code, string Message);
}
