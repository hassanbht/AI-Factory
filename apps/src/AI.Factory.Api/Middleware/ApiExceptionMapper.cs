using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Resilience;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.AspNetCore.Mvc;

namespace AI.Factory.Api.Middleware;

public sealed record ApiErrorMapping(
    int StatusCode,
    string Title,
    string Detail,
    string Code,
    FailureCategory FailureCategory,
    bool ShouldAudit);

/// <summary>
/// Centralized mapper that converts application/domain exceptions into HTTP-specific
/// status codes, safe ProblemDetails messages, and error codes.
/// Keeps HTTP concerns isolated in the API layer without leaking into Domain/Application.
/// </summary>
public sealed class ApiExceptionMapper
{
    private readonly IFailureClassifier _classifier;

    public ApiExceptionMapper(IFailureClassifier? classifier = null)
    {
        _classifier = classifier ?? new FailureClassifier();
    }

    public ApiErrorMapping Map(Exception exception)
    {
        var classification = _classifier.Classify(exception);

        return exception switch
        {
            NotFoundException or KeyNotFoundException => new ApiErrorMapping(
                StatusCodes.Status404NotFound,
                "Resource not found",
                string.IsNullOrWhiteSpace(exception.Message) ? "The requested resource was not found." : exception.Message,
                "not_found",
                FailureCategory.Validation,
                ShouldAudit: false),

            ValidationException or ArgumentException or InvalidTaskPlanException or CircularWorkItemDependencyException => new ApiErrorMapping(
                StatusCodes.Status400BadRequest,
                "Validation failed",
                string.IsNullOrWhiteSpace(exception.Message) ? "The request payload failed validation." : exception.Message,
                "validation_error",
                FailureCategory.Validation,
                ShouldAudit: false),

            ConflictException => new ApiErrorMapping(
                StatusCodes.Status409Conflict,
                "Operation conflict",
                string.IsNullOrWhiteSpace(exception.Message) ? "The operation conflicts with the current state." : exception.Message,
                "conflict",
                exception.Message.Contains("approval", StringComparison.OrdinalIgnoreCase)
                    ? FailureCategory.Approval
                    : FailureCategory.Validation,
                ShouldAudit: false),

            ForbiddenException or UnauthorizedAccessException or SecurityViolationException => new ApiErrorMapping(
                StatusCodes.Status403Forbidden,
                "Forbidden",
                string.IsNullOrWhiteSpace(exception.Message) ? "Access is forbidden." : exception.Message,
                "forbidden",
                FailureCategory.Infrastructure,
                ShouldAudit: false),

            TimeoutException => new ApiErrorMapping(
                StatusCodes.Status504GatewayTimeout,
                "Operation timed out",
                "The operation timed out.",
                "timeout",
                FailureCategory.Timeout,
                ShouldAudit: false),

            OperationCanceledException => new ApiErrorMapping(
                StatusCodes.Status408RequestTimeout,
                "Request cancelled",
                "The operation was cancelled.",
                "timeout",
                FailureCategory.Cancelled,
                ShouldAudit: false),

            CircuitOpenException => new ApiErrorMapping(
                StatusCodes.Status503ServiceUnavailable,
                "Service unavailable",
                "The service or an external dependency is temporarily unavailable.",
                "service_unavailable",
                FailureCategory.Provider,
                ShouldAudit: true),

            BulkheadRejectedException => new ApiErrorMapping(
                StatusCodes.Status429TooManyRequests,
                "Too many requests",
                "The requested operation could not be processed due to concurrency limits.",
                "too_many_requests",
                FailureCategory.Infrastructure,
                ShouldAudit: true),

            _ when classification.IsTransient && (classification.Category == FailureCategory.Provider || classification.Category == FailureCategory.Infrastructure) => new ApiErrorMapping(
                StatusCodes.Status503ServiceUnavailable,
                "Service unavailable",
                "The service or an external dependency is temporarily unavailable.",
                "service_unavailable",
                classification.Category,
                ShouldAudit: true),

            _ when classification.Category == FailureCategory.Timeout => new ApiErrorMapping(
                StatusCodes.Status504GatewayTimeout,
                "Operation timed out",
                "The operation timed out.",
                "timeout",
                FailureCategory.Timeout,
                ShouldAudit: false),

            _ => new ApiErrorMapping(
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred.",
                "internal_error",
                FailureCategory.Unknown,
                ShouldAudit: true)
        };
    }

    public (ApiErrorMapping Mapping, ProblemDetails ProblemDetails) CreateProblemDetails(
        Exception exception,
        HttpContext context,
        string correlationId)
    {
        var mapping = Map(exception);

        var problemDetails = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{mapping.StatusCode}",
            Title = mapping.Title,
            Status = mapping.StatusCode,
            Detail = mapping.Detail,
            Instance = context.Request.Path
        };

        problemDetails.Extensions["correlationId"] = correlationId;
        problemDetails.Extensions["code"] = mapping.Code;
        problemDetails.Extensions["traceId"] = correlationId;
        problemDetails.Extensions["message"] = mapping.Detail;

        return (mapping, problemDetails);
    }
}
