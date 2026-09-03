using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Security;
using AI.Factory.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Operational boundary protection for internal diagnostics, metrics, and audit endpoints.
/// When RequireAccessKey is enabled, requests without a valid operational key are rejected
/// with HTTP 401 Unauthorized, while health checks and normal business endpoints remain unaffected.
/// </summary>
public sealed class OperationalEndpointSecurityMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;
    private readonly IOperationalMetrics? _metrics;
    private readonly ILogger<OperationalEndpointSecurityMiddleware> _logger;

    public OperationalEndpointSecurityMiddleware(
        RequestDelegate next,
        SecurityOptions options,
        ILogger<OperationalEndpointSecurityMiddleware> logger,
        IOperationalMetrics? metrics = null)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.OperationalEndpoints.RequireAccessKey || !IsOperationalPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (await IsValidKeyProvidedAsync(context))
        {
            await _next(context);
            return;
        }

        var correlationId = context.Items.TryGetValue(CorrelationMiddleware.ItemKey, out var itemVal) && itemVal is not null
            ? itemVal.ToString()!
            : context.TraceIdentifier;

        if (!context.Response.Headers.ContainsKey(CorrelationMiddleware.HeaderName))
        {
            context.Response.Headers[CorrelationMiddleware.HeaderName] = correlationId;
        }

        _metrics?.RecordOperationalAccessDenied();
        _logger.LogWarning(
            "Access denied to operational endpoint: Method={Method}, Path={Path}",
            context.Request.Method,
            context.Request.Path);

        var auditLogger = context.RequestServices?.GetService<IAuditLogger>();
        if (auditLogger is not null)
        {
            var executionId = Guid.TryParse(correlationId, out var parsed) ? parsed : Guid.NewGuid();
            try
            {
                await auditLogger.LogAsync(
                    executionId: executionId,
                    eventType: AuditEventType.OperationalAccessDenied,
                    severity: AuditSeverity.Warning,
                    message: $"Operational access denied for {context.Request.Method} {context.Request.Path}",
                    correlationId: executionId,
                    outcome: "Denied",
                    actorType: "Anonymous",
                    cancellationToken: context.RequestAborted);
            }
            catch
            {
                // Observability failure must not crash request flow
            }
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = "https://httpstatuses.com/401",
            Title = "Unauthorized",
            Status = StatusCodes.Status401Unauthorized,
            Detail = "Operational access key is missing or invalid.",
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["code"] = "unauthorized";
        problem.Extensions["traceId"] = correlationId;

        await JsonSerializer.SerializeAsync(context.Response.Body, problem, JsonOptions);
    }

    private async Task<bool> IsValidKeyProvidedAsync(HttpContext context)
    {
        var configuredKey = _options.OperationalEndpoints.AccessKey;
        if (string.IsNullOrWhiteSpace(configuredKey) &&
            !string.IsNullOrWhiteSpace(_options.OperationalEndpoints.AccessKeySecretName))
        {
            var secretProvider = context.RequestServices?.GetService<ISecretProvider>();
            if (secretProvider is not null)
            {
                configuredKey = await secretProvider.GetSecretAsync(
                    _options.OperationalEndpoints.AccessKeySecretName,
                    context.RequestAborted);
            }
        }

        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return false;
        }

        var headerName = _options.OperationalEndpoints.HeaderName;
        string? candidate = null;

        if (!string.IsNullOrWhiteSpace(headerName) &&
            context.Request.Headers.TryGetValue(headerName, out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal))
        {
            candidate = headerVal.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyVal) &&
                 !string.IsNullOrWhiteSpace(apiKeyVal))
        {
            candidate = apiKeyVal.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue("Authorization", out var authVal) &&
                 !string.IsNullOrWhiteSpace(authVal))
        {
            var str = authVal.ToString().Trim();
            if (str.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = str.Substring(7).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        return CryptographicOperations.FixedTimeEquals(candidateBytes, configuredBytes);
    }

    public static bool IsOperationalPath(PathString path)
    {
        var value = path.Value ?? string.Empty;

        return value.StartsWith("/api/operations", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/api/audit-log", StringComparison.OrdinalIgnoreCase) ||
               (value.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase) &&
                value.EndsWith("/diagnostics", StringComparison.OrdinalIgnoreCase));
    }
}
