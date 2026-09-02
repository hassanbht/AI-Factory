# Phase 17.4.1 — Global Exception Handling, Problem Details & API Error Contract

## Architecture

The API pipeline establishes a single, authoritative exception-to-response boundary:

```text
HTTP Request
    ↓
CorrelationMiddleware (reads/generates X-Correlation-ID, opens IOperationScope)
    ↓
GlobalExceptionMiddleware (catches unhandled/mapped exceptions, maps them, logs, writes response)
    ↓
Endpoints / Application Services
    ↓
Exception thrown
    ↓
Failure Classification (IFailureClassifier)
    ↓
HTTP Status Mapping (ApiExceptionMapper)
    ↓
ProblemDetails Response (Microsoft.AspNetCore.Mvc.ProblemDetails)
    ↓
Structured Logging (Method, Path, StatusCode, FailureCategory, CorrelationId, OperationId)
    ↓
Response Headers include matching X-Correlation-ID
```

`CorrelationMiddleware` is registered before `GlobalExceptionMiddleware` in `Program.cs` so that:
1. `CorrelationMiddleware` establishes the correlation ID (from request header or newly generated) and sets the `X-Correlation-ID` response header eagerly.
2. The ambient `IOperationContext` is active throughout request handling.
3. If an exception occurs, `GlobalExceptionMiddleware` catches it while ambient correlation context is still intact.
4. `GlobalExceptionMiddleware` logs structured events containing `CorrelationId`, `OperationId`, and other operation metadata.
5. `GlobalExceptionMiddleware` serializes an RFC-compliant `ProblemDetails` response (`application/problem+json`) where `ProblemDetails.Extensions["correlationId"]` exactly matches `X-Correlation-ID`.
6. When the response finishes, `CorrelationMiddleware` closes the operation scope (marking it failed for 5xx responses).

## Exception Mapping

Mapping is centralized in `AI.Factory.Api.Middleware.ApiExceptionMapper`:

| Exception / Classification | HTTP Status | ProblemDetails Title | Safe Detail | FailureCategory | Audit Event |
|---|---|---|---|---|---|
| `NotFoundException`, `KeyNotFoundException` | 404 Not Found | Resource not found | Safe message from exception | Validation | No |
| `ValidationException`, `ArgumentException`, `InvalidTaskPlanException`, `CircularWorkItemDependencyException` | 400 Bad Request | Validation failed | Safe validation message | Validation | No |
| `ConflictException` | 409 Conflict | Operation conflict | Safe conflict message | Approval / Validation | No |
| `ForbiddenException`, `UnauthorizedAccessException`, `SecurityViolationException` | 403 Forbidden | Forbidden | "Access is forbidden." or safe message | Infrastructure | No |
| `TimeoutException`, or `FailureCategory.Timeout` | 504 Gateway Timeout | Operation timed out | "The operation timed out." | Timeout | No |
| `OperationCanceledException` | 408 Request Timeout | Request cancelled | "The operation was cancelled." | Cancelled | No |
| `CircuitOpenException`, or transient Provider/Infrastructure | 503 Service Unavailable | Service unavailable | "The service or an external dependency is temporarily unavailable." | Provider / Infrastructure | Yes (Error) |
| Unknown / Unhandled `Exception` | 500 Internal Server Error | Internal server error | "An unexpected error occurred." (sanitized) | Unknown | Yes (Error) |

## Correlation Behavior

- **Incoming Correlation ID**: If `X-Correlation-ID` is provided (e.g. `X-Correlation-ID: test-correlation-123` or a GUID), it is preserved verbatim in `context.Items["CorrelationId"]`, echoed in the response header `X-Correlation-ID`, and serialized in `ProblemDetails.Extensions["correlationId"]`. Non-GUID strings are projected into deterministic GUIDs internally for the SQLite/EF Core `AuditLogEntry.CorrelationId` column without requiring schema migrations.
- **Generated Correlation ID**: If omitted, a new GUID is generated and echoed identically in `X-Correlation-ID` and `ProblemDetails.Extensions["correlationId"]`.
- **Guaranteed Match**: The response header `X-Correlation-ID` and `ProblemDetails.correlationId` always match.

## Structured Logging

- **5xx Unexpected Failures**: Logged via `_logger.LogError(exception, ...)` with structured placeholders:
  `Method`, `Path`, `StatusCode`, `FailureCategory`, `CorrelationId`, `OperationId`, `ProjectId`, `TaskId`, `RunId`.
- **Cancellations**: Logged via `_logger.LogInformation(...)` as informational events, never as errors or infrastructure failures.
- **4xx Client Failures**: Logged via `_logger.LogWarning(exception, ...)` without polluting error dashboards.
- **Redaction**: Stack traces, connection strings, provider response bodies, internal file system paths, and raw SQL are never exposed in the response payload. In production, 500 errors always return `"An unexpected error occurred."`.

## Audit Behavior

- Handled 4xx client errors (not found, validation, conflict, forbidden) and normal request cancellations do not create audit log entries.
- Unhandled 500 server errors and 503 infrastructure/provider outages outside the orchestrator create an audit log entry via `IAuditLogger.LogAsync` (`AuditEventType.WorkflowFailed`, `AuditSeverity.Error`, outcome: `"Failed"`, actor: `"System"`).
- Failures inside the background orchestration pipeline are handled and audited by the orchestrator itself, preventing duplicate audit entries in middleware.

## Extension Points

1. **New Application Exceptions**: Add them to `AI.Factory.Application.Exceptions`. They remain HTTP-agnostic.
2. **New Mapping Rules**: Extend `ApiExceptionMapper.Map(Exception)` in `AI.Factory.Api.Middleware` without modifying `GlobalExceptionMiddleware` or Domain/Application layers.
3. **Failure Classification**: Conservative additions can be registered in `FailureClassifier.Classify(Exception)`.
