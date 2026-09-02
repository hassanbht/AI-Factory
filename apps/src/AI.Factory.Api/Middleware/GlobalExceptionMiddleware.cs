using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Single place exceptions become HTTP responses. Controllers/services never
/// translate exceptions to status codes themselves — they throw domain/application
/// exceptions (or let unexpected exceptions propagate) and this middleware maps them
/// into standard RFC-compatible ProblemDetails responses with correlation tracking,
/// structured logging, and audit trail integration.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions ProblemDetailsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly ApiExceptionMapper _mapper;
    private readonly IOperationContextAccessor? _contextAccessor;
    private readonly IAuditLogger? _auditLogger;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        ApiExceptionMapper? mapper = null,
        IOperationContextAccessor? contextAccessor = null)
        : this(next, logger, mapper, contextAccessor, auditLogger: null)
    {
    }

    internal GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        ApiExceptionMapper? mapper,
        IOperationContextAccessor? contextAccessor,
        IAuditLogger? auditLogger)
    {
        _next = next;
        _logger = logger;
        _mapper = mapper ?? new ApiExceptionMapper();
        _contextAccessor = contextAccessor;
        _auditLogger = auditLogger;
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
        var correlationId = ResolveCorrelationId(context);

        if (!context.Response.Headers.ContainsKey(CorrelationMiddleware.HeaderName))
        {
            context.Response.Headers[CorrelationMiddleware.HeaderName] = correlationId;
        }

        var (mapping, problemDetails) = _mapper.CreateProblemDetails(exception, context, correlationId);
        var ambient = _contextAccessor?.Current;

        if (mapping.StatusCode >= 500)
        {
            _logger.LogError(
                exception,
                "HTTP request failed unexpectedly: Method={Method}, Path={Path}, StatusCode={StatusCode}, FailureCategory={FailureCategory}, CorrelationId={CorrelationId}, OperationId={OperationId}, ProjectId={ProjectId}, TaskId={TaskId}, RunId={RunId}",
                context.Request.Method,
                context.Request.Path,
                mapping.StatusCode,
                mapping.FailureCategory,
                correlationId,
                ambient?.OperationId,
                ambient?.ProjectId,
                ambient?.TaskId,
                ambient?.RunId);
        }
        else if (mapping.FailureCategory == FailureCategory.Cancelled)
        {
            _logger.LogInformation(
                "HTTP request was cancelled: Method={Method}, Path={Path}, StatusCode={StatusCode}, FailureCategory={FailureCategory}, CorrelationId={CorrelationId}",
                context.Request.Method,
                context.Request.Path,
                mapping.StatusCode,
                mapping.FailureCategory,
                correlationId);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "HTTP request failed: Method={Method}, Path={Path}, StatusCode={StatusCode}, FailureCategory={FailureCategory}, CorrelationId={CorrelationId}, OperationId={OperationId}",
                context.Request.Method,
                context.Request.Path,
                mapping.StatusCode,
                mapping.FailureCategory,
                correlationId,
                ambient?.OperationId);
        }

        if (mapping.ShouldAudit)
        {
            var auditLogger = _auditLogger ?? context.RequestServices?.GetService<IAuditLogger>();
            if (auditLogger is not null)
            {
                await RecordAuditAsync(context, auditLogger, mapping, correlationId, ambient);
            }
        }

        if (context.Response.HasStarted)
        {
            _logger.LogWarning("Response has already started, cannot write ProblemDetails response.");
            return;
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = mapping.StatusCode;

        await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails, ProblemDetailsOptions));
    }

    private string ResolveCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(CorrelationMiddleware.ItemKey, out var itemVal) && itemVal is not null)
        {
            var str = itemVal.ToString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str;
            }
        }

        if (context.Request.Headers.TryGetValue(CorrelationMiddleware.HeaderName, out var headerVal))
        {
            var str = headerVal.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(str))
            {
                return str;
            }
        }

        var ambient = _contextAccessor?.Current;
        if (ambient is not null && ambient.CorrelationId != Guid.Empty)
        {
            return ambient.CorrelationId.ToString();
        }

        return context.TraceIdentifier;
    }

    private async Task RecordAuditAsync(
        HttpContext context,
        IAuditLogger auditLogger,
        ApiErrorMapping mapping,
        string correlationId,
        OperationContext? ambient)
    {
        try
        {
            var executionId = Guid.TryParse(correlationId, out var parsed)
                ? parsed
                : ambient?.CorrelationId ?? Guid.NewGuid();

            await auditLogger.LogAsync(
                executionId: executionId,
                eventType: AuditEventType.WorkflowFailed,
                severity: AuditSeverity.Error,
                message: $"HTTP {context.Request.Method} {context.Request.Path} failed: {mapping.Title}",
                projectId: ambient?.ProjectId,
                workItemId: ambient?.TaskId,
                correlationId: executionId,
                operationId: ambient?.OperationId,
                runId: ambient?.RunId,
                outcome: "Failed",
                actorType: "System",
                cancellationToken: context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audit log entry for unhandled API exception.");
        }
    }
}
