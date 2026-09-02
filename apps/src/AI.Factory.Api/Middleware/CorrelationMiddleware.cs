using System.Security.Cryptography;
using System.Text;
using AI.Factory.Application.Abstractions;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// One IOperationScope per HTTP request — reads X-Correlation-ID if the
/// caller supplied one, otherwise generates one; every _auditLogger.LogAsync
/// call made while handling this request inherits it via the ambient
/// IOperationContext. Registered before GlobalExceptionMiddleware so
/// exception handling and ProblemDetails responses have full correlation context.
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly IOperationScopeFactory _scopeFactory;

    public CorrelationMiddleware(RequestDelegate next, IOperationScopeFactory scopeFactory)
    {
        _next = next;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rawCorrelation = context.Request.Headers.TryGetValue(HeaderName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue.ToString())
                ? headerValue.ToString().Trim()
                : null;

        Guid scopeGuid;
        string correlationIdString;

        if (rawCorrelation is not null)
        {
            correlationIdString = rawCorrelation;
            if (!Guid.TryParse(rawCorrelation, out scopeGuid))
            {
                // Deterministically project the non-GUID correlation string into a valid Guid
                // so the underlying IOperationScope / EF Core model can persist it without schema changes.
                var hash = MD5.HashData(Encoding.UTF8.GetBytes(rawCorrelation));
                scopeGuid = new Guid(hash);
            }
        }
        else
        {
            scopeGuid = Guid.NewGuid();
            correlationIdString = scopeGuid.ToString();
        }

        context.Items[ItemKey] = correlationIdString;
        context.Response.Headers[HeaderName] = correlationIdString;

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = correlationIdString;
            }

            return Task.CompletedTask;
        });

        await using var scope = _scopeFactory.Begin(
            $"HTTP {context.Request.Method} {context.Request.Path}", correlationId: scopeGuid);

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
