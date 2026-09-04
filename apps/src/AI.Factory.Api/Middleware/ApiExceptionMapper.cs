using System.Data.Common;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Resilience;
using AI.Factory.Application.Security;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
                SensitiveDataSanitizer.Sanitize(string.IsNullOrWhiteSpace(exception.Message) ? "The requested resource was not found." : exception.Message),
                "not_found",
                FailureCategory.Validation,
                ShouldAudit: false),

            ValidationException or ArgumentException or InvalidTaskPlanException or CircularWorkItemDependencyException or BadHttpRequestException or System.Text.Json.JsonException or FormatException => new ApiErrorMapping(
                StatusCodes.Status400BadRequest,
                "Validation failed",
                SensitiveDataSanitizer.Sanitize(string.IsNullOrWhiteSpace(exception.Message) ? "The request payload failed validation." : exception.Message),
                "validation_error",
                FailureCategory.Validation,
                ShouldAudit: false),

            DbUpdateConcurrencyException or ConcurrencyConflictException => new ApiErrorMapping(
                StatusCodes.Status409Conflict,
                "Resource was modified",
                "The resource was changed by another operation. Please reload and retry.",
                "concurrency_conflict",
                FailureCategory.Validation,
                ShouldAudit: false),

            ConflictException => new ApiErrorMapping(
                StatusCodes.Status409Conflict,
                "Operation conflict",
                SensitiveDataSanitizer.Sanitize(string.IsNullOrWhiteSpace(exception.Message) ? "The operation conflicts with the current state." : exception.Message),
                "conflict",
                exception.Message.Contains("approval", StringComparison.OrdinalIgnoreCase)
                    ? FailureCategory.Approval
                    : FailureCategory.Validation,
                ShouldAudit: false),

            ForbiddenException or UnauthorizedAccessException or SecurityViolationException => new ApiErrorMapping(
                StatusCodes.Status403Forbidden,
                "Forbidden",
                SensitiveDataSanitizer.Sanitize(string.IsNullOrWhiteSpace(exception.Message) ? "Access is forbidden." : exception.Message),
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

            Microsoft.Data.Sqlite.SqliteException sqliteEx when sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6 || sqliteEx.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) || sqliteEx.Message.Contains("locked", StringComparison.OrdinalIgnoreCase) => new ApiErrorMapping(
                StatusCodes.Status503ServiceUnavailable,
                "Database unavailable",
                "The database service is temporarily unavailable. Please retry later.",
                "database_unavailable",
                FailureCategory.Infrastructure,
                ShouldAudit: true),

            DbUpdateException dbEx when IsUniqueConstraintViolation(dbEx) => new ApiErrorMapping(
                StatusCodes.Status409Conflict,
                "Resource already exists",
                "A resource with the specified unique field already exists.",
                "conflict",
                FailureCategory.Validation,
                ShouldAudit: false),

            Microsoft.Data.Sqlite.SqliteException sqliteEx when IsUniqueConstraintViolation(sqliteEx) => new ApiErrorMapping(
                StatusCodes.Status409Conflict,
                "Resource already exists",
                "A resource with the specified unique field already exists.",
                "conflict",
                FailureCategory.Validation,
                ShouldAudit: false),

            DbUpdateException or DbException => new ApiErrorMapping(
                StatusCodes.Status500InternalServerError,
                "Database error",
                "A database error occurred.",
                "database_error",
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
        var sanitizedDetail = SensitiveDataSanitizer.Sanitize(mapping.Detail);

        var problemDetails = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{mapping.StatusCode}",
            Title = mapping.Title,
            Status = mapping.StatusCode,
            Detail = sanitizedDetail,
            Instance = context.Request.Path
        };

        problemDetails.Extensions["correlationId"] = correlationId;
        problemDetails.Extensions["code"] = mapping.Code;
        problemDetails.Extensions["traceId"] = correlationId;
        problemDetails.Extensions["message"] = sanitizedDetail;

        return (mapping, problemDetails);
    }

    public static bool IsUniqueConstraintViolation(Exception? ex)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx)
        {
            if (sqliteEx.SqliteErrorCode == 19 &&
                (sqliteEx.SqliteExtendedErrorCode == 2067 || // SQLITE_CONSTRAINT_UNIQUE
                 sqliteEx.SqliteExtendedErrorCode == 1555 || // SQLITE_CONSTRAINT_PRIMARYKEY
                 sqliteEx.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (ex.InnerException is not null && IsUniqueConstraintViolation(ex.InnerException))
        {
            return true;
        }

        return false;
    }
}
