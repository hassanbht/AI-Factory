using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Security;
using AI.Factory.Domain.Enums;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Enforces request body size bounds against the configured MaxRequestBodySizeBytes.
/// Rejects oversized requests early with HTTP 413 Payload Too Large and standard ProblemDetails.
/// </summary>
public sealed class RequestSizeLimitMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILogger<RequestSizeLimitMiddleware> _logger;

    public RequestSizeLimitMiddleware(
        RequestDelegate next,
        SecurityOptions options,
        ILogger<RequestSizeLimitMiddleware> logger,
        IOperationalMetrics? metrics = null)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var maxLimit = _options.RequestLimits.MaxRequestBodySizeBytes;

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is not null && !sizeFeature.IsReadOnly)
        {
            sizeFeature.MaxRequestBodySize = maxLimit;
        }

        if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > maxLimit)
        {
            var correlationId = context.Items.TryGetValue(CorrelationMiddleware.ItemKey, out var itemVal) && itemVal is not null
                ? itemVal.ToString()!
                : context.TraceIdentifier;

            if (!context.Response.Headers.ContainsKey(CorrelationMiddleware.HeaderName))
            {
                context.Response.Headers[CorrelationMiddleware.HeaderName] = correlationId;
            }

            _metrics?.RecordSecurityRequestRejected();
            _logger.LogWarning(
                "Request body rejected because it exceeds size limit: Method={Method}, Path={Path}, ContentLength={Length}, MaxLimit={MaxLimit}",
                context.Request.Method,
                context.Request.Path,
                context.Request.ContentLength.Value,
                maxLimit);

            var auditLogger = context.RequestServices?.GetService<IAuditLogger>();
            if (auditLogger is not null)
            {
                var executionId = Guid.TryParse(correlationId, out var parsed) ? parsed : Guid.NewGuid();
                try
                {
                    await auditLogger.LogAsync(
                        executionId: executionId,
                        eventType: AuditEventType.SecurityRequestRejected,
                        severity: AuditSeverity.Warning,
                        message: $"Request rejected: payload size {context.Request.ContentLength.Value} bytes exceeds {maxLimit} bytes limit",
                        correlationId: executionId,
                        outcome: "Rejected",
                        actorType: "Anonymous",
                        cancellationToken: context.RequestAborted);
                }
                catch
                {
                    // Observability failure must not crash request flow
                }
            }

            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Type = "https://httpstatuses.com/413",
                Title = "Payload Too Large",
                Status = StatusCodes.Status413PayloadTooLarge,
                Detail = $"The request payload size exceeds the maximum allowed limit of {maxLimit} bytes.",
                Instance = context.Request.Path
            };
            problem.Extensions["correlationId"] = correlationId;
            problem.Extensions["code"] = "payload_too_large";
            problem.Extensions["traceId"] = correlationId;

            await JsonSerializer.SerializeAsync(context.Response.Body, problem, JsonOptions);
            return;
        }

        await _next(context);
    }
}
