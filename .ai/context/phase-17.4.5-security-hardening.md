# Phase 17.4.5 — Security Hardening

## Overview

Phase 17.4.5 implements comprehensive security hardening across the AI Factory platform, protecting HTTP ingress, operational diagnostics, request payloads, and persistent audit logs while maintaining local developer velocity and container orchestration compatibility.

---

## 1. Threat Surface Reviewed

The AI Factory platform exposes HTTP endpoints that control code execution, background agent workers, build/test pipelines, approval workflows, and operational diagnostics. The following attack surfaces were analyzed and hardened:
- **Public Infrastructure Endpoints**: Health probes (`/health/live`, `/health/ready`, `/health`) could be subjected to denial-of-service or leak infrastructure dependencies.
- **Internal Operational Diagnostics**: Endpoints (`/api/operations/*`, `/api/audit-log`, `/api/projects/{projectId}/diagnostics`) expose sensitive operational metrics, in-flight operations, circuit breaker states, and error summaries.
- **Business Orchestration Boundaries**: Mutation endpoints (`POST /api/projects`, `POST /api/orchestration-runs`, approval decisions) trigger resource-intensive agent tasks and could be abused via payload flooding, path traversal, or resource exhaustion.
- **Error Responses (ProblemDetails)**: Unhandled server errors and database exceptions could expose stack traces, database schema, SQLite file paths, or provider credentials.
- **Execution Boundaries**: External input used in workspace paths or process arguments could lead to directory escape or command injection if not bounded.

---

## 2. Endpoint Classification

Endpoints are classified into four distinct security tiers:

| Tier | Endpoints | Protection & Policies |
|---|---|---|
| **PUBLIC_INFRASTRUCTURE** | `/health/live`, `/health/ready`, `/health` | Unauthenticated, exempt from rate limiting and operational access keys, no server banner leakage. |
| **INTERNAL_OPERATIONAL** | `/api/operations/metrics`, `/api/operations/resilience`, `/api/operations/recent`, `/api/operations/{correlationId}`, `/api/operations/active`, `/api/audit-log`, `/api/projects/{projectId}/diagnostics` | `OperationalApi` rate limiting policy. Protected by `X-Operations-Key` (or `X-Api-Key` / `Bearer`) when `Security:OperationalEndpoints:RequireAccessKey` is enabled. |
| **BUSINESS_ORCHESTRATION** | `/api/projects`, `/api/projects/{id}`, `/api/approvals/*`, `/api/orchestration-runs/*` | `GeneralApi` or `ExpensiveOperation` rate limiting policy, input validation bounds, request body size limits. |
| **DEVELOPMENT_ONLY** | `/openapi`, `/openapi/v1.json` | Conditionally exposed via `Security:OpenApi:Enabled` (defaults to `IsDevelopment()`). |

---

## 3. Rate Limiting Policies

Implemented using ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting`:
- **`GeneralApi`**: Default fixed-window rate limiter for standard business requests (default: 100 permits / 60 seconds).
- **`OperationalApi`**: Stricter fixed-window limiter for telemetry, audit log queries, and diagnostic snapshots (default: 50 permits / 60 seconds).
- **`ExpensiveOperation`**: Strict fixed-window limiter for triggering background pipeline runs via `POST /api/orchestration-runs` (default: 10 permits / 60 seconds).
- **Rejection Behavior**:
  - HTTP Status: `429 Too Many Requests`.
  - Header: `Retry-After: <seconds>`.
  - Header: `X-Correlation-ID` preserved and matched in ProblemDetails.
  - Body: RFC-compliant `application/problem+json` (`code: "too_many_requests"`).
  - Observability: Records `RateLimitRejected` audit event and operational metric counter.
  - Exemption: Health probes (`/health/*`) are never throttled.

---

## 4. Request Size Limits

- **Global Kestrel Enforcement**: `KestrelServerOptions.Limits.MaxRequestBodySize = Security:RequestLimits:MaxRequestBodySizeBytes` (default: 262,144 bytes / 256 KB).
- **Early Rejection Middleware (`RequestSizeLimitMiddleware`)**: Inspects `Content-Length` before buffering payloads. Rejects oversized requests with HTTP 413 Payload Too Large and standard ProblemDetails (`code: "payload_too_large"`).
- **Observability**: Records `SecurityRequestRejected` audit event and operational metric counter.

---

## 5. Validation Hardening

Audited and hardened all incoming routes, parameters, and payloads:
- **Route Identifiers**: Route parameters expecting GUIDs are validated as non-empty GUIDs; empty GUIDs (`00000000-0000-0000-0000-000000000000`) and malformed strings immediately reject with HTTP 400 Bad Request (`code: "validation_error"`).
- **Pagination & Bounds**:
  - `page`: Must be `>= 1` (negative values rejected).
  - `pageSize`: Must be between `1` and `100` (exceeding limits rejected).
  - `count`: Must be between `1` and `1000` (negative or excessive values rejected).
- **Enums**: Enum query parameters (such as `severity` in `/api/audit-log`) are strictly validated against `Enum.IsDefined`.
- **String Lengths & Characters**:
  - Project name bounded to 200 characters; path traversal characters (`..`, illegal filename characters) rejected.
  - Project description and approval comments bounded to 2000 characters.
  - `MaxAttempts` on orchestration runs bounded to `1..10`.

---

## 6. Security Headers

Applied consistently to all HTTP responses via `SecurityHeadersMiddleware`:
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: no-referrer`
- `Permissions-Policy: accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()`
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (exempted on `/openapi` so Swagger/OpenAPI UI functions properly).
- **Banner Stripping**: `Server` and `X-Powered-By` headers are removed from all responses; `KestrelServerOptions.AddServerHeader = false`.

---

## 7. HTTPS / Transport Security

- **HSTS**: Enabled in non-development environments via `app.UseHsts()` when `Security:Https:EnableHstsInProduction` is true.
- **HTTPS Redirection**: Configurable via `Security:Https:RedirectHttpToHttps` (default: false in dev so local testing remains seamless).
- **Forwarded Headers**: When `Security:ForwardedHeaders:Enabled` is true, explicitly binds `X-Forwarded-For` and `X-Forwarded-Proto` without trusting arbitrary proxies by default.

---

## 8. Operational Endpoint Protection

- **Access Key Guard (`OperationalEndpointSecurityMiddleware`)**: When `Security:OperationalEndpoints:RequireAccessKey` is true, access to `/api/operations/*`, `/api/audit-log`, and `/api/projects/{projectId}/diagnostics` requires a valid access key.
- **Supported Headers**: `X-Operations-Key`, `X-Api-Key`, or `Authorization: Bearer <key>`.
- **Timing Attack Prevention**: Keys are verified using constant-time comparison (`CryptographicOperations.FixedTimeEquals`).
- **Safety**: Unmatched or missing keys return HTTP 401 Unauthorized with standard ProblemDetails. Secrets are never logged.
- **Health Checks**: Public infrastructure health probes remain accessible without access keys.

---

## 9. Sensitive Data Protection

- **Centralized Redactor (`SensitiveDataSanitizer`)**:
  - Redacts bearer tokens (`Bearer [REDACTED]`).
  - Redacts secret keys, passwords, credentials, tokens (`[REDACTED]`).
  - Redacts database connection strings (`Data Source=[REDACTED]`).
  - Redacts local filesystem paths (`[REDACTED_PATH]`) from error details and logs.
- **Audit Logging**: `AuditLogger` automatically sanitizes free-text messages and metadata dictionaries before persistence.

---

## 10. ProblemDetails Security Rules

Centralized in `ApiExceptionMapper`:
- **Production Safety**: 500 Internal Server Error returns `"An unexpected error occurred."` Stack traces are never serialized.
- **Database Error Isolation**: `DbUpdateException`, `DbUpdateConcurrencyException`, and `DbException` are intercepted; SQL queries, table schemas, and SQLite file paths are replaced with safe generic messages (`"A database error occurred."` / `"A database concurrency conflict occurred."`).
- **Sanitized Details**: All exception messages passed into ProblemDetails are passed through `SensitiveDataSanitizer`.

---

## 11. OpenAPI Production Behavior

- Configurable via `Security:OpenApi:Enabled`.
- When set to `false`, OpenAPI endpoints return 404 Not Found.
- When `null`, defaults to `app.Environment.IsDevelopment()`.

---

## 12. CORS Behavior

- Configurable via `Security:Cors:Enabled` and `Security:Cors:AllowedOrigins`.
- When enabled, origins are strictly matched against configured absolute URIs.
- Disallowed origins do not receive `Access-Control-Allow-Origin` headers.

---

## 13. Middleware Pipeline Order

The API middleware pipeline executes in the following deterministic sequence:
1. `CorrelationMiddleware` — Establishes `X-Correlation-ID` and ambient `IOperationScope`.
2. `SecurityHeadersMiddleware` — Injects security headers and removes server headers on all responses.
3. `GlobalExceptionMiddleware` — Catches unhandled downstream exceptions and translates them into RFC ProblemDetails.
4. `ForwardedHeaders` — (If enabled) processes proxy headers.
5. `Hsts` & `HttpsRedirection` — (If configured / in production) enforces HTTPS transport security.
6. `Cors` — (If enabled) evaluates origin policies.
7. `RequestSizeLimitMiddleware` — Rejects oversized payloads before processing (HTTP 413).
8. `ShutdownMiddleware` — Rejects traffic during graceful shutdown (HTTP 503).
9. `RateLimiter` — Enforces built-in rate limits (`GeneralApi`, `OperationalApi`, `ExpensiveOperation`).
10. `OperationalEndpointSecurityMiddleware` — Validates access keys on operational routes.
11. Endpoints & Route Handlers — Health checks, Projects, Approvals, Audit log, Diagnostics, Orchestration runs.

---

## 14. Security Audit Events

Added to `AuditEventType`:
- `OperationalAccessDenied`: Logged when an operational endpoint is accessed without valid credentials.
- `RateLimitRejected`: Logged when a request is rejected by rate limiting policies.
- `SecurityRequestRejected`: Logged when a request violates security limits (e.g., payload size bounds).

---

## 15. Security Operational Metrics

Added to `IOperationalMetrics` and `OperationalMetricsSnapshot`:
- `RateLimitRejected`: Count of HTTP 429 rejections.
- `SecurityRequestRejected`: Count of security boundary rejections (e.g., HTTP 413).
- `OperationalAccessDenied`: Count of HTTP 401 unauthorized operational access attempts.

---

## 16. Configuration

In `appsettings.json`:
```json
"Security": {
  "OperationalEndpoints": {
    "RequireAccessKey": false,
    "AccessKey": "",
    "HeaderName": "X-Operations-Key"
  },
  "RateLimiting": {
    "Enabled": true,
    "General": {
      "PermitLimit": 100,
      "WindowSeconds": 60,
      "QueueLimit": 0
    },
    "Operational": {
      "PermitLimit": 50,
      "WindowSeconds": 60,
      "QueueLimit": 0
    },
    "Expensive": {
      "PermitLimit": 10,
      "WindowSeconds": 60,
      "QueueLimit": 0
    }
  },
  "RequestLimits": {
    "MaxRequestBodySizeBytes": 262144
  },
  "Headers": {
    "EnableContentSecurityPolicy": true,
    "ContentSecurityPolicy": "default-src 'none'; frame-ancestors 'none'",
    "FrameOptions": "DENY",
    "ReferrerPolicy": "no-referrer",
    "PermissionsPolicy": "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()"
  },
  "Cors": {
    "Enabled": false,
    "AllowedOrigins": []
  },
  "OpenApi": {
    "Enabled": null
  },
  "Https": {
    "RedirectHttpToHttps": false,
    "EnableHstsInProduction": true
  },
  "ForwardedHeaders": {
    "Enabled": false
  }
}
```

Startup validation verifies:
- `RequireAccessKey = true` without a key (or key < 16 chars) throws `InvalidOperationException`.
- `MaxRequestBodySizeBytes <= 0` throws `InvalidOperationException`.
- Negative or zero rate limiting limits throw `InvalidOperationException`.
- Malformed CORS origins throw `InvalidOperationException`.

---

## 17. Known Limitations & Trust Assumptions

- **Operational Key vs IAM**: The operational access key provides a lightweight network/cluster boundary for operational endpoints; it is not a full user identity/RBAC/OAuth server.
- **Reverse Proxy / TLS Termination**: Production TLS certificates are assumed to be terminated by an ingress controller, API gateway, or reverse proxy.
- **Agent Sandbox**: The system relies on `WorkspacePathGuard` and `CommandPolicyOptions` for workspace and command restrictions; physical sandboxing (e.g. gVisor, Firecracker) remains an infrastructure-level concern.
