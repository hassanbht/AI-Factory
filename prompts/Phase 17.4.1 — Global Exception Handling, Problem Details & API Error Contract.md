# Phase 17.4.1 — Global Exception Handling, Problem Details & API Error Contract

## Objective

Implement a centralized, production-oriented API error handling system for AI Factory.

The system must:

1. Convert known application failures into correct HTTP status codes.
2. Return RFC-compatible `ProblemDetails` responses.
3. Preserve correlation IDs introduced in Phase 17.3.
4. Integrate with the existing `IFailureClassifier`.
5. Produce structured logs without leaking sensitive implementation details.
6. Avoid duplicating existing concepts.
7. Keep existing successful API behavior unchanged.
8. Add automated tests for all important mappings.

This phase is deliberately focused on **HTTP/API exception handling only**.

Do NOT introduce unrelated production infrastructure such as:

- Polly
- Retry policies
- Circuit breakers
- OpenTelemetry
- Prometheus
- Grafana
- ELK
- Authentication redesign
- Rate limiting
- New message brokers
- CQRS
- MediatR

Those belong to later phases.

---

# 0. Mandatory Pre-Work

Before making ANY changes:

1. Read `AGENTS.md`.
2. Read all relevant files under:

```text
.ai/context/
```

At minimum inspect:

```text
.ai/context/architecture.md
.ai/context/decisions.md
.ai/context/phase-17.1-*.md
.ai/context/phase-17.2-*.md
.ai/context/phase-17.3-observability.md
```

3. Inspect the existing API composition root:

```text
apps/src/AI.Factory.Api/Program.cs
```

4. Inspect the existing middleware implementation:

```text
apps/src/AI.Factory.Api/Middleware/
```

In particular inspect:

```text
GlobalExceptionMiddleware.cs
CorrelationMiddleware.cs
```

5. Inspect:

```text
IFailureClassifier
FailureClassificationResult
FailureClassifier
FailureCategory
AuditSeverity
AuditEventType
AuditLogger
IAuditLogger
IOperationContextAccessor
IOperationalMetrics
```

6. Inspect the existing API endpoints and identify current exception behavior.

Specifically inspect endpoints involving:

- Projects
- Work items
- Approval requests
- Approve
- Reject
- Orchestration execution
- Diagnostics

7. Search the entire solution for:

```text
throw new InvalidOperationException
throw new ArgumentException
throw new KeyNotFoundException
ValidationException
```

Do not assume the exact exception set before inspecting the repository.

8. Search all existing tests for:

```text
WebApplicationFactory
ProblemDetails
GlobalExceptionMiddleware
ApiIntegrationTests
```

Reuse existing testing conventions.

---

# 1. Core Architectural Rule

There must be exactly one primary HTTP exception-to-response boundary.

The intended flow is:

```text
HTTP Request
    ↓
CorrelationMiddleware
    ↓
GlobalExceptionMiddleware
    ↓
Endpoint / Application Service
    ↓
Exception
    ↓
Failure Classification
    ↓
HTTP Status Mapping
    ↓
ProblemDetails Response
    ↓
Structured Log
    ↓
Response includes Correlation ID
```

Do not add per-endpoint `try/catch` blocks unless an endpoint has genuinely unique behavior.

The primary solution must be centralized middleware.

---

# 2. Review Existing GlobalExceptionMiddleware Before Replacing Anything

An existing `GlobalExceptionMiddleware` may already exist.

Do NOT blindly create a second middleware.

First determine:

- What exceptions it currently catches.
- Whether it returns `ProblemDetails`.
- Whether it already writes structured logs.
- Whether it has access to correlation IDs.
- Whether it currently exposes exception messages.
- Whether it is registered before or after `CorrelationMiddleware`.

Then improve the existing middleware.

Do not create:

```text
ExceptionMiddleware
ApiExceptionMiddleware
ErrorMiddleware
GlobalErrorMiddleware
```

if `GlobalExceptionMiddleware` already exists.

Extend the existing concept.

---

# 3. Standard Error Response Contract

All handled API errors must return a consistent `ProblemDetails` response.

Use ASP.NET Core's standard:

```text
Microsoft.AspNetCore.Mvc.ProblemDetails
```

Do not invent a custom error DTO unless the existing architecture already requires one.

The response should contain at minimum:

```json
{
  "type": "https://httpstatuses.com/500",
  "title": "Internal server error",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "instance": "/api/...",
  "correlationId": "..."
}
```

The exact `type` URL may follow the existing project convention if one already exists.

The response must include:

```text
correlationId
```

using the correlation ID from Phase 17.3.

Do not generate a second unrelated correlation ID.

The source of truth is the existing operation context / correlation middleware.

---

# 4. Exception-to-HTTP Mapping

Implement a centralized mapping policy.

The exact mapping must be based on the actual exception types already present in the solution.

At minimum support the following semantic categories.

## 4.1 Not Found

Examples:

- Requested entity does not exist.
- Project does not exist.
- Work item does not exist.
- Approval request does not exist.

Return:

```text
HTTP 404 Not Found
```

ProblemDetails title:

```text
Resource not found
```

Do not expose internal repository details.

---

## 4.2 Validation Failure

Examples:

- Invalid user input.
- Invalid state transition caused by invalid request.
- Application validation failure.
- Known validation exceptions.

Return:

```text
HTTP 400 Bad Request
```

ProblemDetails title:

```text
Validation failed
```

Validation details may be returned only when they are safe for API consumers.

Do not expose stack traces.

---

## 4.3 Conflict

Examples:

- Approval already approved.
- Approval already rejected.
- Entity already in a terminal state.
- Operation cannot be performed because current state conflicts with requested state.

Return:

```text
HTTP 409 Conflict
```

ProblemDetails title:

```text
Operation conflict
```

This is particularly important for the existing approval endpoints.

Current behavior where such cases become unhandled HTTP 500 responses must be fixed centrally.

---

## 4.4 Cancellation

If the request is cancelled:

Do not treat it as an internal server failure.

Use the project's existing cancellation conventions if they exist.

Do not fabricate a special API status code unless the existing architecture already uses one.

The key requirement is:

- Do not log cancellation as an Error.
- Do not classify normal request cancellation as Infrastructure failure.
- Preserve cancellation propagation.

If no explicit response can be safely written because the client disconnected, allow the request pipeline to terminate cleanly.

---

## 4.5 Timeout

If a recognized timeout occurs:

Return:

```text
HTTP 504 Gateway Timeout
```

Only map genuine timeout exceptions to this status.

Do not classify arbitrary failures as timeout.

ProblemDetails title:

```text
Operation timed out
```

---

## 4.6 Infrastructure / Provider Failure

Known infrastructure failures should map conservatively.

Examples may include:

- Database unavailable.
- External provider unavailable.
- Agent provider unavailable.

Return:

```text
HTTP 503 Service Unavailable
```

only when the failure is genuinely transient infrastructure/provider related.

Do NOT convert every unknown exception to 503.

---

## 4.7 Unknown Failure

Unknown exceptions must return:

```text
HTTP 500 Internal Server Error
```

The client must receive a safe generic message.

For example:

```text
An unexpected error occurred.
```

Do NOT expose:

- Stack traces
- Connection strings
- API keys
- File paths
- Internal exception messages
- Provider credentials
- Raw SQL

Full details may appear in server logs only.

---

# 5. Use IFailureClassifier Correctly

Phase 17.3 introduced:

```text
IFailureClassifier
FailureClassificationResult
FailureCategory
```

Reuse it.

Do not create another classifier such as:

```text
IApiExceptionClassifier
IHttpFailureClassifier
IExceptionMapper
```

unless inspection proves the existing abstraction fundamentally cannot be extended.

The preferred design is:

```text
Exception
    ↓
IFailureClassifier
    ↓
FailureCategory
    ↓
HTTP mapping
```

However, HTTP status mapping is NOT necessarily the same thing as `FailureCategory`.

For example:

```text
Validation → 400
Approval conflict → 409
Unknown → 500
Provider → 503
Timeout → 504
```

Keep:

```text
Failure classification
```

separate from:

```text
HTTP response mapping
```

but reuse the existing classifier as input.

---

# 6. Logging Rules

Every handled unexpected failure should be logged using structured logging.

At minimum include:

```text
CorrelationId
OperationId
ProjectId
TaskId
RunId
FailureCategory
HTTP Status Code
Request Path
HTTP Method
```

Use structured logging placeholders.

Example conceptually:

```text
HTTP request failed.
CorrelationId={CorrelationId}
StatusCode={StatusCode}
FailureCategory={FailureCategory}
Path={Path}
```

Do NOT concatenate structured values into one string.

Avoid:

```csharp
_logger.LogError("Error " + correlationId + " " + exception.Message);
```

Prefer structured logging.

---

# 7. Audit Trail Integration

Do not create a duplicate audit system.

Phase 17.3 already introduced the extended audit trail.

Reuse:

```text
IAuditLogger
AuditEventType
FailureCategory
CorrelationId
Outcome
ActorType
```

Determine whether an unhandled API exception should create an audit entry.

The rule should be:

- Expected validation/not-found responses should not automatically create noisy Error audit events unless the existing audit convention requires it.
- Unexpected failures should be auditable.
- Infrastructure/provider failures that affect an operation should be auditable.
- Do not create duplicate audit entries when the same failure was already logged by the orchestrator.

Avoid double-counting metrics and duplicate audit records.

If the existing architecture already logs the failure at a deeper orchestration layer, the middleware should log HTTP context without creating a redundant duplicate audit event unless necessary.

Document the final rule.

---

# 8. Correlation ID Requirements

The correlation behavior from Phase 17.3 must remain unchanged.

Required behavior:

## Incoming correlation ID

If:

```text
X-Correlation-ID
```

is provided:

- Preserve it.
- Use it in logs.
- Return it in the response.

## Missing correlation ID

If missing:

- Generate one using the existing Phase 17.3 mechanism.
- Return it in the response.
- Include it in ProblemDetails.

## Exception response

Even on failure:

```text
X-Correlation-ID
```

must be returned when a response can be written.

ProblemDetails must also contain:

```text
correlationId
```

The header and body values must match.

Do not generate two IDs.

---

# 9. Middleware Ordering

Inspect current middleware ordering.

The desired conceptual order is:

```text
CorrelationMiddleware
        ↓
GlobalExceptionMiddleware
        ↓
Other middleware
        ↓
Endpoints
```

This ensures:

- Exceptions have a correlation context.
- Exception logs include correlation ID.
- Error responses can include correlation ID.

Do not reorder unrelated middleware without reason.

If the existing pipeline requires a slightly different technical order, preserve correctness and document why.

---

# 10. Environment-Specific Detail Exposure

Error responses must be safe in Production.

## Development

Detailed errors may be available through existing development tooling, but do not create a new mechanism that leaks secrets.

## Production

Never expose raw exception details.

At minimum hide:

```text
Exception.Message
StackTrace
InnerException details
Connection strings
Provider response bodies containing credentials
File system paths
```

The API client should receive a safe ProblemDetails response.

---

# 11. Known InvalidOperationException Problem

The existing codebase uses `InvalidOperationException` for multiple semantic meanings.

Examples may include:

```text
Not found
Already approved
Already rejected
Invalid state
```

Do NOT blindly map every:

```text
InvalidOperationException
```

to the same HTTP status.

First inspect all usages.

If multiple meanings exist, choose the smallest safe architectural improvement.

Preferred options, in order:

## Option A

Use existing domain/application exception types if already present.

## Option B

Introduce a small number of semantic application exceptions ONLY if necessary.

For example:

```text
NotFoundException
ConflictException
ValidationException
```

Do not introduce a large exception hierarchy.

## Option C

If existing services already expose enough context to distinguish the failure safely, centralize mapping without widespread refactoring.

Do NOT inspect only one endpoint and add special-case string matching in middleware like:

```csharp
if (exception.Message.Contains("already approved"))
```

unless absolutely unavoidable and documented as technical debt.

Prefer typed semantics.

---

# 12. Minimal Semantic Exception Types

Only if inspection proves necessary, introduce minimal application-level exceptions.

Possible types:

```text
NotFoundException
ConflictException
```

They must:

- Live in the appropriate Application layer.
- Not depend on ASP.NET Core.
- Not depend on HTTP status codes.
- Carry safe semantic information.
- Avoid excessive inheritance.

Do not create:

```text
ProjectNotFoundException
WorkItemNotFoundException
ApprovalNotFoundException
ProjectConflictException
ApprovalConflictException
...
```

unless the existing architecture already uses that style.

Prefer generic semantic exceptions.

Update services that currently use ambiguous `InvalidOperationException` only where needed.

Do not perform a repository-wide exception rewrite unless required.

---

# 13. ProblemDetails Factory / Mapping Helper

Avoid putting a huge switch statement with hundreds of lines directly inside middleware.

If the existing architecture supports it, introduce a small focused helper.

For example:

```text
ApiExceptionMapper
```

or similar.

But only create it if it improves clarity.

The responsibility should be:

```text
Exception
+ FailureClassificationResult
    ↓
HTTP status
ProblemDetails title
Safe detail
```

The mapper must remain HTTP/API specific.

Do not put HTTP status code knowledge inside:

```text
Domain
Application
IFailureClassifier
```

The Application layer should remain HTTP-agnostic.

---

# 14. Metrics Behavior

Phase 17.3 added:

```text
IOperationalMetrics
```

Do not distort metrics.

Rules:

- A 404 is not automatically an infrastructure failure.
- A validation failure is not automatically an operation failure unless it represents a failed operation.
- A cancelled client request is not automatically a failed agent execution.
- An HTTP 500 from middleware should not double-count an operation failure already recorded by the orchestrator.

Inspect current metric recording.

Only add metrics integration where it is unambiguous.

Do not add a generic:

```text
HttpFailures++
```

counter unless the existing metrics abstraction is explicitly extended for it.

This phase is about correct error behavior, not expanding the metrics model unnecessarily.

---

# 15. API Endpoints That Must Be Verified

At minimum inspect and test endpoints related to:

```text
/api/projects
/api/approvals
/api/operations
/api/projects/{projectId}/diagnostics
```

Exact routes must come from the current `Program.cs`.

Test both successful and failure paths.

At minimum verify:

1. Unknown project returns 404 where applicable.
2. Invalid approval ID returns correct status.
3. Approving an already decided request returns 409.
4. Rejecting an already decided request returns 409.
5. Validation failure returns 400.
6. Unexpected exception returns safe 500.
7. Incoming correlation ID is preserved on an error response.
8. Generated correlation ID appears on an error response.
9. Error ProblemDetails contains the same correlation ID as the response header.

Do not invent endpoints solely for tests.

Use existing endpoints and test hooks.

---

# 16. Testing Requirements

Follow the existing testing conventions.

Inspect the current test project structure.

Likely relevant projects include:

```text
AI.Factory.Api.Tests
AI.Factory.Application.Tests
AI.Factory.Worker.Tests
```

Do not create redundant test projects.

---

## 16.1 Middleware / API Tests

Test through the real API pipeline using the existing:

```text
WebApplicationFactory
```

approach if already used.

Required scenarios:

### Not Found

```text
Expected:
404
ProblemDetails
CorrelationId present
```

### Validation

```text
Expected:
400
Safe validation response
CorrelationId present
```

### Conflict

```text
Expected:
409
ProblemDetails
No unhandled 500
```

### Timeout

If there is a safe deterministic way to trigger timeout classification:

```text
504
```

Do not create a flaky test that waits for real timeouts.

### Unexpected Exception

```text
500
```

Response must NOT contain:

```text
Stack trace
Internal exception text
File paths
```

### Correlation Preservation

Request:

```http
X-Correlation-ID: test-correlation-123
```

Response:

```http
X-Correlation-ID: test-correlation-123
```

ProblemDetails:

```json
{
  "correlationId": "test-correlation-123"
}
```

### Generated Correlation

When no header is supplied:

- Response header contains a correlation ID.
- ProblemDetails contains a correlation ID.
- Both values match.

---

## 16.2 Unit Tests

If a dedicated mapping component is created, test it directly.

Required mapping coverage:

```text
Known NotFound
Known Validation
Known Conflict
Known Timeout
Known Provider/Infrastructure
Unknown Exception
```

Do not only test HTTP endpoints if mapping logic is independently testable.

---

# 17. Avoid Test-Only Production Backdoors

Do not add:

```text
if (environment.IsTesting)
```

logic into production services.

If tests need controlled failure behavior, use the existing dependency injection and test doubles.

For example:

- Replace a service implementation in `WebApplicationFactory`.
- Inject a controlled failing dependency.
- Use an existing test-only endpoint only if one already exists.

Do not create production API endpoints such as:

```text
/api/test/throw
```

---

# 18. Backward Compatibility

Existing successful API responses must remain unchanged.

Do not change:

- Existing route paths.
- Existing success response DTOs.
- Existing database schema unless semantic exceptions require no schema anyway.
- Existing correlation mechanism.
- Existing audit schema.
- Existing operation metrics semantics.

This phase should primarily affect failure behavior.

---

# 19. Database

This phase should normally require:

```text
NO database migration.
```

Do not add a migration unless inspection reveals a genuinely required persistence change.

Semantic exception handling and ProblemDetails should not require database changes.

If a migration is somehow introduced, explain exactly why.

---

# 20. Dependency Injection

Do not create duplicate registrations.

Reuse existing services.

Potential additions should be minimal.

If a mapper/helper is introduced:

```text
Scoped
```

or:

```text
Singleton
```

depending on whether it has state/dependencies.

Do not make everything Singleton automatically.

Follow the lifetime conventions already used by the project.

Verify the real API composition root resolves successfully.

If Worker has no HTTP API exception handling need, do not force HTTP-specific services into Worker.

Keep API-specific concerns in the API project.

---

# 21. Documentation

Create or update:

```text
.ai/context/phase-17.4.1-api-error-handling.md
```

Document:

## Architecture

```text
Request
    ↓
Correlation
    ↓
Exception Middleware
    ↓
Failure Classifier
    ↓
HTTP Mapping
    ↓
ProblemDetails
```

## Exception mapping

Document the final mapping table.

## Correlation

Document how correlation IDs are included in error responses.

## Logging

Document:

- What gets logged.
- What is intentionally not exposed.

## Audit behavior

Document when middleware creates an audit event and when it deliberately avoids duplication.

## Extension points

Document how future exception types can be mapped.

---

# 22. Build and Test Commands

After implementation run:

```powershell
dotnet build apps\AI.Factory.slnx
```

Then:

```powershell
dotnet test apps\AI.Factory.slnx --blame-hang-timeout 90s
```

If your repository is now located somewhere else, detect and use the actual solution path.

Do not claim success without running the commands.

---

# 23. Required Final Report

After implementation, provide a concise but complete report.

Include:

## 1. Summary

What changed.

## 2. Exception Mapping

A table:

| Failure | HTTP Status |
|---|---|
| Not Found | 404 |
| Validation | 400 |
| Conflict | 409 |
| Timeout | 504 |
| Infrastructure | 503 |
| Unknown | 500 |

Only include mappings actually implemented.

## 3. Files Created

List exact paths.

## 4. Files Modified

List exact paths and why.

## 5. Correlation Behavior

Explain:

```text
Request Header
↓
Operation Context
↓
Logs
↓
ProblemDetails
↓
Response Header
```

## 6. Security

Explain what internal information is deliberately hidden.

## 7. Audit Behavior

Explain whether middleware creates audit events and how duplicate audit entries are avoided.

## 8. Tests

Provide exact test result.

For example:

```text
Total: X
Passed: X
Failed: 0
```

## 9. Build

Provide exact build result.

## 10. Intentional Limitations

List anything deliberately deferred.

Do not hide limitations.

---

# Acceptance Criteria

The phase is complete only when all of the following are true:

- [ ] Existing `GlobalExceptionMiddleware` is reused or improved instead of duplicated.
- [ ] All handled API errors return standard `ProblemDetails`.
- [ ] Error responses include the Phase 17.3 correlation ID.
- [ ] `X-Correlation-ID` response header matches `ProblemDetails.correlationId`.
- [ ] Not-found failures return 404.
- [ ] Validation failures return 400.
- [ ] Approval/state conflicts return 409.
- [ ] Genuine timeout failures return 504.
- [ ] Genuine transient infrastructure/provider failures can return 503.
- [ ] Unknown failures return safe 500.
- [ ] Internal stack traces are never exposed in production responses.
- [ ] Existing `IFailureClassifier` is reused.
- [ ] HTTP concerns are not moved into Domain/Application.
- [ ] Existing successful API behavior remains unchanged.
- [ ] No unnecessary database migration is introduced.
- [ ] No duplicate audit system is created.
- [ ] No duplicate exception middleware is created.
- [ ] API integration tests cover important failure paths.
- [ ] Correlation ID behavior is tested for error responses.
- [ ] `dotnet build` succeeds.
- [ ] Full `dotnet test` succeeds.
- [ ] Phase documentation is updated.

---

# Important Implementation Philosophy

Prefer the smallest compatible improvement.

This system already has:

```text
FailureClassifier
AuditLogger
OperationContext
CorrelationMiddleware
GlobalExceptionMiddleware
OperationalMetrics
```

Phase 17.4.1 must connect and refine these existing building blocks.

Do not redesign the architecture.

Do not introduce parallel abstractions.

Do not perform broad refactoring unrelated to API failure handling.

Make the system more reliable by completing the missing integration between:

```text
Exception
    ↓
Failure Classification
    ↓
HTTP Status
    ↓
ProblemDetails
    ↓
Correlation
    ↓
Safe Logging
```