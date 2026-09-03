# Phase 17.4.5 — Security Hardening

You are continuing implementation of the AI Factory platform.

This is an existing production-oriented Clean Architecture solution.

Do NOT redesign the architecture.
Do NOT replace working abstractions.
Do NOT introduce duplicate security concepts.
Extend the existing implementation conservatively.

==================================================
PRE-WORK — MANDATORY
==================================================

Before making ANY changes:

1. Read AGENTS.md completely.
2. Read all relevant files under .ai/context/.
3. Read the implementation context and reports for:
   - Phase 17.1
   - Phase 17.2
   - Phase 17.3
   - Phase 17.4.1
   - Phase 17.4.2
   - Phase 17.4.3
   - Phase 17.4.4
4. Inspect the current API pipeline.
5. Inspect existing authentication/authorization capabilities before introducing any new ones.
6. Inspect all API endpoints.
7. Inspect diagnostics and operational endpoints.
8. Inspect existing logging, audit logging, ProblemDetails, exception handling and configuration.

Do not assume an authentication system exists.

If authentication/authorization already exists:
extend and harden it.

If it does not exist:
do NOT silently build a large Identity/OAuth/JWT platform unless required by the existing architecture or clearly necessary for a production boundary.

The goal of this phase is security hardening of the current system.

==================================================
CURRENT PLATFORM
==================================================

The system currently includes:

- ASP.NET Core API
- Background Worker
- Clean Architecture
- EF Core
- SQLite
- Autonomous coding orchestration
- Agent execution
- Provider execution
- Build/Test/Repair execution
- Human approval
- Evaluation and learning
- Structured logging
- Audit logging
- Correlation IDs
- Operation scopes
- Operational diagnostics
- Global exception handling
- RFC ProblemDetails
- Retry and timeout handling
- Circuit breakers
- Bulkhead isolation
- Graceful shutdown
- Cancellation propagation
- Background worker reliability
- Health checks

Phase 17.4.4 is complete and must not regress.

==================================================
GOAL
==================================================

Implement:

Phase 17.4.5 — Security Hardening

The implementation should address:

1. HTTP security hardening
2. Sensitive data protection
3. Request validation hardening
4. Request size and abuse protection
5. Rate limiting where appropriate
6. Operational endpoint protection
7. Safe diagnostics exposure
8. Secure logging and audit behavior
9. Safe ProblemDetails responses
10. Security-focused configuration validation
11. Security headers
12. HTTPS production behavior review
13. Dependency and middleware boundary review

The system must remain usable for local development.

==================================================
SECTION 1 — SECURITY THREAT REVIEW
==================================================

Before implementing, inspect the actual platform and identify the realistic attack surface.

Review:

- public API endpoints
- health endpoints
- diagnostics endpoints
- approval endpoints
- project/task endpoints
- orchestration endpoints
- any endpoint that triggers agent/build/test/repair work
- OpenAPI endpoints
- exception responses
- logging
- audit logging
- configuration
- database error exposure

Document the identified attack surface.

Do not invent threats unrelated to this platform.

==================================================
SECTION 2 — API ENDPOINT SECURITY CLASSIFICATION
==================================================

Classify endpoints into categories such as:

PUBLIC_INFRASTRUCTURE
- /health/live
- possibly /health/ready depending deployment requirements

INTERNAL_OPERATIONAL
- /api/operations/*
- diagnostics
- resilience
- active operations

BUSINESS_ORCHESTRATION
- project/task/orchestration endpoints
- approval actions
- operations that trigger autonomous execution

DEVELOPMENT_ONLY
- OpenAPI endpoints if applicable

Do not assume all endpoints require identical protection.

Create the smallest reusable endpoint security policy mechanism compatible with the existing application.

Do not duplicate ASP.NET Core authorization concepts.

==================================================
SECTION 3 — RATE LIMITING AND ABUSE PROTECTION
==================================================

Inspect whether ASP.NET Core built-in rate limiting is already used.

If not, implement rate limiting using the built-in ASP.NET Core rate limiting facilities.

Do NOT introduce a third-party rate limiting framework unless already present.

Requirements:

1. Global protection against abusive traffic.
2. Stricter limits for expensive operation-triggering endpoints.
3. Stricter limits for diagnostics/operational endpoints where appropriate.
4. Health endpoints must not be accidentally blocked by normal rate limiting.
5. Rejections must return a consistent safe response.

Use configuration-driven policies.

Suggested categories:

GeneralApi
OperationalApi
ExpensiveOperation

Do not hard-code production-only limits without configuration.

Rate limit rejection should:

- return 429 Too Many Requests
- include Retry-After when possible
- preserve X-Correlation-ID
- not expose internal implementation details
- use the existing ProblemDetails/error contract where feasible

Do not apply rate limiting to the Worker.

==================================================
SECTION 4 — REQUEST SIZE LIMITS
==================================================

Inspect all endpoints.

Configure reasonable request body limits.

Requirements:

- API must reject excessively large bodies.
- Limits must be configurable.
- Large requests must not cause unnecessary memory allocation.
- Uploads should only receive larger limits if the platform actually supports uploads.
- Do not invent file upload support.

Where possible:

- enforce global limits
- allow explicit endpoint overrides only when justified

Return safe HTTP responses for rejected requests.

==================================================
SECTION 5 — INPUT VALIDATION HARDENING
==================================================

Audit all externally reachable inputs.

Review:

- route parameters
- query parameters
- request bodies
- GUID parsing
- pagination
- limits/count parameters
- free-form strings
- model/provider identifiers
- project/task identifiers
- approval decisions
- orchestration configuration

Requirements:

1. Reject malformed identifiers.
2. Reject invalid enum values.
3. Bound pagination.
4. Bound query limits.
5. Reject negative values.
6. Reject unreasonable values.
7. Validate required fields.
8. Avoid passing unchecked user strings into sensitive execution paths.

Reuse existing validation mechanisms.

Do not introduce a second validation framework if FluentValidation or another existing mechanism already exists.

If no validation framework exists, use minimal existing ASP.NET/Application patterns.

==================================================
SECTION 6 — SAFE PATH / COMMAND / EXECUTION BOUNDARIES
==================================================

This platform can perform agent/build/test/repair operations.

Inspect all boundaries where externally derived values could influence:

- filesystem paths
- shell commands
- process execution
- build commands
- test commands
- repository paths
- provider endpoints

Requirements:

- Do not concatenate untrusted input into shell commands.
- Do not allow arbitrary executable paths from HTTP input.
- Do not allow path traversal.
- Normalize and validate filesystem paths where applicable.
- Restrict execution to configured/known workspace roots.
- Preserve existing legitimate build/test behavior.

If the current implementation already has safe abstractions:

Reuse them.

Do not invent a sandbox/container platform in this phase.

Document remaining execution trust assumptions.

==================================================
SECTION 7 — SECURITY HEADERS
==================================================

Implement production-appropriate HTTP security headers.

Review current middleware ordering.

Consider headers such as:

- X-Content-Type-Options: nosniff
- X-Frame-Options
- Referrer-Policy
- Content-Security-Policy where applicable
- Permissions-Policy

Do not blindly add a CSP that breaks API/OpenAPI behavior.

Since this is primarily an API:

use conservative headers appropriate to an API.

Do not add browser-specific policies that break Swagger/OpenAPI UI without verifying.

Requirements:

- configurable where appropriate
- no duplicate headers
- applied consistently
- testable

Do not expose framework/version information unnecessarily.

==================================================
SECTION 8 — HTTPS / TRANSPORT SECURITY
==================================================

Review existing HTTPS configuration.

Requirements:

- production configuration must support HTTPS correctly
- development must remain usable
- do not silently redirect in a way that breaks local development
- HSTS should be production-only
- do not claim TLS certificates are managed by this application

Inspect reverse proxy assumptions.

Do not introduce a reverse proxy.

If forwarded headers are required by deployment assumptions:

make behavior explicit and configurable.

Do not blindly trust all forwarded headers from arbitrary clients.

==================================================
SECTION 9 — SENSITIVE DATA PROTECTION
==================================================

Audit all logging and diagnostics paths.

Identify sensitive data including:

- API keys
- provider tokens
- secrets
- passwords
- connection strings
- authorization headers
- cookies
- raw provider credentials
- potentially sensitive command arguments

Requirements:

These must not appear in:

- structured logs
- audit logs
- ProblemDetails
- diagnostics endpoints
- metrics endpoints
- health check responses

Create or extend a reusable sanitization/redaction mechanism only if necessary.

Do not duplicate redaction logic in every endpoint.

Requirements:

- redact known sensitive configuration keys
- redact Authorization headers
- avoid logging full request bodies unless already necessary and safe
- avoid logging raw exception details to API clients
- retain enough internal information for debugging

Use:

public response -> sanitized
internal structured log -> useful but redacted

==================================================
SECTION 10 — PROBLEMDETAILS SECURITY REVIEW
==================================================

Review:

- GlobalExceptionMiddleware
- ApiExceptionMapper
- ProblemDetails extensions

Ensure:

Production responses must not expose:

- stack traces
- database SQL
- connection strings
- file system paths
- internal provider credentials
- raw internal exception messages for unknown exceptions

Known validation/not-found/conflict messages may remain safe when explicitly designed.

Development behavior may provide additional diagnostics only if it is already an intentional project convention.

Do not make production error responses opaque if safe actionable details are available.

==================================================
SECTION 11 — DIAGNOSTICS ENDPOINT HARDENING
==================================================

Review endpoints such as:

- /api/operations/metrics
- /api/operations/recent
- /api/operations/{correlationId}
- /api/projects/{projectId}/diagnostics
- /api/operations/resilience
- /api/operations/active

These may reveal:

- project state
- operation metadata
- failure information
- dependency state
- infrastructure topology

Requirements:

Determine whether these endpoints are:

1. public
2. internal
3. protected

based on actual current deployment/auth architecture.

If no authentication exists:

Do not pretend an endpoint is protected.

Implement a minimal explicit protection mechanism only if compatible with the current architecture.

Possible configuration-based option:

Security:
  OperationalEndpoints:
    RequireAccessKey: true/false

Only consider this if the project currently has no authentication and operational endpoints need protection.

If adding such a mechanism:

- use secure secret configuration
- constant-time comparison where appropriate
- never log the secret
- document that this is an operational boundary, not a replacement for full identity/authentication

Do not add hard-coded credentials.

Health endpoints should remain compatible with container orchestration.

==================================================
SECTION 12 — OPENAPI / DEVELOPMENT ENDPOINT SECURITY
==================================================

Inspect OpenAPI exposure.

Requirements:

- production exposure must be configurable
- development defaults may remain convenient
- do not expose unnecessary development diagnostics in production
- avoid exposing internal-only schemas unnecessarily

Do not remove OpenAPI unless configuration requires it.

Use existing application configuration conventions.

==================================================
SECTION 13 — CORS REVIEW
==================================================

Inspect whether CORS is currently configured.

If CORS is not configured:

Do not add permissive AllowAnyOrigin blindly.

If browser clients require CORS:

Use explicit configuration.

Requirements:

- no AllowAnyOrigin + credentials combination
- allowed origins configurable
- development defaults documented
- production must not silently allow every origin

==================================================
SECTION 14 — DATABASE ERROR HARDENING
==================================================

Review EF Core and database exception flow.

Requirements:

- database exceptions must not reach clients raw
- SQLite paths must not be exposed in ProblemDetails
- SQL/EF implementation details must remain internal
- logging should retain diagnostic value without leaking secrets

Reuse:

- GlobalExceptionMiddleware
- ApiExceptionMapper
- FailureClassifier

Do not create duplicate exception middleware.

==================================================
SECTION 15 — SECURITY CONFIGURATION VALIDATION
==================================================

Review configuration values related to:

- operational endpoint access
- allowed origins
- request limits
- rate limits
- security headers
- OpenAPI exposure
- forwarded headers
- HTTPS/HSTS

Requirements:

- validate configuration at startup
- reject obviously unsafe combinations where appropriate
- provide clear startup error messages
- do not silently enable insecure production behavior

Examples:

- RequireAccessKey enabled but no key configured -> startup validation failure
- invalid rate limit values -> startup validation failure
- negative request size -> startup validation failure
- malformed configured origin -> startup validation failure

Do not validate secrets by logging them.

==================================================
SECTION 16 — AUDIT AND SECURITY EVENTS
==================================================

Reuse existing audit infrastructure.

Add security audit events only when meaningful.

Examples may include:

- OperationalAccessDenied
- RateLimitRejected
- SecurityRequestRejected

Do not add events for every normal request.

Requirements:

- security events must not store secrets
- correlation ID should be present where available
- metadata must be sanitized
- do not generate excessive audit noise

==================================================
SECTION 17 — OPERATIONAL METRICS
==================================================

Extend existing metrics only where useful.

Possible counters:

- RateLimitRejected
- SecurityRequestRejected
- OperationalAccessDenied

Do not add counters unless there is a clear operational use.

Metrics must remain:

- thread-safe
- in-memory
- snapshot-based

Do not introduce Prometheus/OpenTelemetry in this phase.

==================================================
SECTION 18 — MIDDLEWARE ORDER
==================================================

Review the actual middleware pipeline carefully.

Preserve:

- correlation ID propagation
- graceful shutdown gating
- global exception handling
- health behavior
- rate limiting behavior

Security middleware must not prevent:

- correlation ID generation
- consistent ProblemDetails
- graceful shutdown behavior

Document the final middleware ordering.

Do not rearrange middleware without understanding the impact.

==================================================
SECTION 19 — TEST REQUIREMENTS
==================================================

Add automated tests.

At minimum:

### Security headers

- expected headers present
- headers not duplicated
- API behavior remains correct

### Rate limiting

- requests within limit succeed
- excess requests return 429
- correlation ID remains present
- health endpoints remain usable

### Request validation

- invalid GUID rejected safely
- negative pagination rejected
- excessive limit rejected
- invalid enum rejected

### Request size

- oversized request rejected
- normal request succeeds

### Sensitive data

Verify that:

- authorization headers are not returned
- secrets are not returned in ProblemDetails
- diagnostics do not expose secrets
- exception responses do not expose stack traces

### Operational endpoints

- protected endpoints reject unauthorized access if protection is enabled
- access succeeds with valid configured credentials
- access secret is not logged

### OpenAPI

- development exposure works according to configuration
- production exposure follows configuration

### CORS

If CORS exists:

- allowed origin succeeds
- disallowed origin is not allowed

### HTTPS/HSTS

Test only deterministic application behavior.

Do not create environment-dependent TLS tests.

==================================================
SECTION 20 — DATABASE MIGRATIONS
==================================================

Do not change the database schema unless truly necessary.

Security hardening should preferably avoid schema changes.

If a schema change is genuinely required:

1. Update entity configuration.
2. Generate an EF Core migration.
3. Do not manually edit generated migrations unless necessary.

==================================================
SECTION 21 — DOCUMENTATION
==================================================

Create:

.ai/context/phase-17.4.5-security-hardening.md

Document:

1. Threat surface reviewed
2. Endpoint classification
3. Rate limiting policies
4. Request size limits
5. Validation hardening
6. Security headers
7. HTTPS/HSTS behavior
8. Operational endpoint protection
9. Sensitive data redaction
10. ProblemDetails security rules
11. OpenAPI production behavior
12. CORS behavior
13. Middleware order
14. Security audit events
15. Security metrics
16. Configuration
17. Known limitations and trust assumptions

==================================================
SECTION 22 — NON-GOALS
==================================================

Do NOT implement unless already strongly implied by the existing project:

- complete OAuth server
- Identity platform
- multi-user RBAC platform
- JWT authentication system from scratch
- external IAM integration
- WAF
- reverse proxy
- TLS certificate management
- secret vault platform
- Kubernetes network policies
- sandboxed container execution

Those are outside this phase.

==================================================
SECTION 23 — IMPLEMENTATION ORDER
==================================================

Perform work in this order:

1. Inspect current attack surface.
2. Inspect middleware pipeline.
3. Inspect authentication/authorization capabilities.
4. Classify endpoints.
5. Review existing input validation.
6. Harden execution boundaries.
7. Add request size limits.
8. Add/configure rate limiting.
9. Add security headers.
10. Harden ProblemDetails.
11. Harden diagnostics exposure.
12. Review OpenAPI.
13. Review CORS.
14. Review HTTPS/HSTS/forwarded headers.
15. Add sensitive-data redaction where needed.
16. Add startup configuration validation.
17. Add audit/metrics where useful.
18. Add automated tests.
19. Update documentation.
20. Build.
21. Run focused tests.
22. Run full test suite.
23. Fix all regressions.

==================================================
SECTION 24 — REQUIRED VERIFICATION
==================================================

Run:

dotnet build apps/AI.Factory.slnx

Then:

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Do not report completion unless:

- build succeeds
- there are no compilation errors
- security tests pass
- all existing tests pass
- no secrets are exposed by diagnostics or ProblemDetails
- Phase 17.4.1–17.4.4 behavior remains intact
- graceful shutdown behavior still works
- correlation IDs remain preserved
- health endpoints remain operational

==================================================
FINAL REPORT
==================================================

At the end provide:

1. Summary of security changes
2. Threat surface findings
3. Files created
4. Files modified
5. Middleware ordering
6. Rate limiting policies
7. Request limits
8. Validation hardening
9. Execution boundary protections
10. Security headers
11. HTTPS/HSTS behavior
12. Operational endpoint protection
13. Sensitive-data protection
14. ProblemDetails security changes
15. OpenAPI/CORS behavior
16. Configuration added
17. Audit events added
18. Metrics added
19. Database migrations
20. Tests added
21. Build result
22. Full test result
23. Intentionally deferred security work
24. Known security limitations and trust assumptions

Do not claim any security control is implemented unless it actually exists in the codebase.