# Phase 17.4.6 — Configuration & Secret Management

You are implementing Phase 17.4.6 of the AI Factory production hardening roadmap.

Repository root:

C:\AI-Factory

Before making ANY changes:

1. Read AGENTS.md completely.
2. Read all relevant files under:
   - .ai/context/
3. Read the previous phase documentation, especially:
   - .ai/context/phase-17.4.1-global-exception-handling.md
   - .ai/context/phase-17.4.2-retry-timeout-resilience.md
   - .ai/context/phase-17.4.3-circuit-breaker-bulkhead.md
   - .ai/context/phase-17.4.4-graceful-shutdown.md
   - .ai/context/phase-17.4.5-security-hardening.md
4. Inspect the existing configuration structure in:
   - apps/src/AI.Factory.Api/appsettings.json
   - apps/src/AI.Factory.Api/appsettings.Development.json
   - apps/src/AI.Factory.Worker/appsettings.json
   - apps/src/AI.Factory.Worker/appsettings.Development.json
   - Docker-related configuration
   - docker-compose files
   - environment variable usage
   - any existing Options classes
   - any existing configuration validation
   - SecurityOptions
   - resilience options
   - provider/model configuration
   - database configuration
5. Search the entire repository for:
   - GetConnectionString
   - IConfiguration
   - GetSection
   - Bind
   - Configure<
   - IOptions<
   - IOptionsMonitor<
   - Environment.GetEnvironmentVariable
   - API keys
   - Authorization headers
   - Bearer tokens
   - connection strings
   - passwords
   - secrets

Do not blindly introduce duplicate configuration abstractions.

Before each logical implementation step, inspect:

1. Who imports the type being changed?
2. Whether the change is breaking.
3. Whether configuration already exists elsewhere.
4. Whether an equivalent abstraction already exists.
5. Whether a secret could accidentally be persisted, logged, audited, exposed through diagnostics, or returned from an API.

Follow the existing Clean Architecture and repository conventions.

Do not introduce MediatR, CQRS, Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, Kubernetes, Docker Swarm, or other infrastructure dependencies unless they are already present or explicitly justified by the existing architecture.

The goal of this phase is to establish a provider-neutral, production-safe configuration and secret management architecture.

======================================================================
PHASE GOALS
======================================================================

Implement a production-oriented configuration and secret management system with:

1. Strongly typed configuration.
2. Centralized configuration validation.
3. Fail-fast startup validation for required production configuration.
4. Safe handling of secrets.
5. Prevention of secrets appearing in:
   - logs
   - audit entries
   - ProblemDetails
   - diagnostics endpoints
   - metrics
   - health endpoints
6. Environment variable support.
7. Development-friendly local secret support.
8. Production-ready secret provider abstraction.
9. Secret rotation compatibility.
10. Configuration diagnostics without exposing values.
11. Configuration precedence documentation.
12. API and Worker consistency.
13. Comprehensive automated tests.

Do not build a fake secret management system.

Do not store secrets in SQLite.

Do not add secrets to appsettings.json.

Do not expose raw configuration values through operational endpoints.

======================================================================
SECTION 1 — CONFIGURATION INVENTORY
======================================================================

Before modifying anything, inspect the existing configuration.

Create a clear inventory of configuration categories already used by the system.

At minimum inspect:

- Database
- Security
- Operational endpoint access key
- Rate limiting
- Request size limits
- Resilience
- Retry
- Timeout
- Circuit breakers
- Bulkheads
- Provider configuration
- Model routing
- OpenRouter / AI providers
- Hermes / agent execution if present
- Worker configuration
- Health checks
- Logging

Do not create duplicate option classes where equivalent ones already exist.

If options are currently read directly through IConfiguration in services, migrate gradually toward typed options only where justified.

Do not perform unrelated refactoring.

======================================================================
SECTION 2 — CONFIGURATION ARCHITECTURE
======================================================================

Establish a consistent configuration architecture.

Preferred structure:

Application-level configuration contracts/options:

AI.Factory.Application/Configuration/

Infrastructure/provider-specific configuration:

AI.Factory.Infrastructure/Configuration/

API/Worker composition roots remain responsible for binding configuration.

The Domain layer must not depend on IConfiguration or Microsoft.Extensions.Options.

Rules:

- Domain must remain configuration-independent.
- Application options should be plain strongly typed classes.
- Infrastructure-specific options belong in Infrastructure.
- API and Worker bind configuration through composition roots.
- Validation must happen before the application begins accepting work.

Reuse existing options such as SecurityOptions where appropriate.

Do not move existing classes merely for cosmetic reasons.

======================================================================
SECTION 3 — STRONGLY TYPED OPTIONS
======================================================================

Identify configuration currently represented as:

- magic strings
- raw IConfiguration access
- Environment.GetEnvironmentVariable calls
- anonymous dictionaries
- loosely typed configuration objects

Introduce strongly typed options where justified.

At minimum ensure production-safe typed configuration exists for:

1. DatabaseOptions
   - Do not expose raw connection strings through diagnostics.
   - Support configuration through standard .NET configuration providers.

2. SecretManagementOptions
   Suggested fields:
   - Enabled
   - Provider
   - FailFastOnMissingSecret
   - ReloadOnChange where supported

3. Provider credential configuration
   Credentials themselves must NOT become normal configuration values exposed by options diagnostics.

4. Operational security configuration
   Reuse SecurityOptions if it already owns these settings.

5. Existing resilience options
   Reuse existing classes.

Avoid duplicate representations of the same setting.

======================================================================
SECTION 4 — SECRET ABSTRACTION
======================================================================

Create a provider-neutral secret abstraction.

Suggested Application abstraction:

ISecretProvider

Suggested operations:

Task<string?> GetSecretAsync(
    string secretName,
    CancellationToken cancellationToken = default);

or equivalent.

Requirements:

- No secret values are logged.
- No secret values are included in exception messages.
- No secret values are written to AuditLogEntry.
- No secret values are returned by API endpoints.
- Missing secrets must produce safe errors.
- Secret provider implementation must support cancellation.
- Secret names may be logged only after sanitization and only if useful.

Provide a result type if needed, for example:

SecretResolutionResult

with safe metadata such as:

- Found
- ProviderName
- LastResolvedUtc

Do NOT include the secret value in diagnostics metadata.

======================================================================
SECTION 5 — INITIAL SECRET PROVIDERS
======================================================================

Implement only providers justified by the current platform.

At minimum:

### 1. EnvironmentSecretProvider

Resolves secrets from environment variables.

Requirements:

- Works in API.
- Works in Worker.
- Works in Docker/container environments.
- Supports cancellation where possible.
- Does not cache secrets unnecessarily.

### 2. DevelopmentSecretProvider

Only if needed by the existing development workflow.

It may integrate with:

- .NET User Secrets

ONLY if the repository structure and SDK setup make this appropriate.

Do not require User Secrets in production.

### 3. CompositeSecretProvider

Implement only if multiple providers are genuinely needed.

Suggested resolution order:

1. Explicit production provider.
2. Environment variables.
3. Development-only provider.

Do not silently fall back in production when configuration explicitly requires fail-fast behavior.

The exact implementation should follow existing architecture and dependency conventions.

======================================================================
SECTION 6 — SECRET REFERENCE CONVENTION
======================================================================

Secrets must be referenced by name, not embedded directly in normal configuration.

For example, configuration may contain:

"ApiKeySecretName": "OPENROUTER_API_KEY"

but must not contain:

"ApiKey": "actual-secret-value"

Apply this principle to external provider credentials.

Where existing appsettings files contain actual credential values:

- Remove them.
- Replace them with secret references or environment variable references.
- Do not print the removed secret values in logs or reports.

Do not invent credentials.

======================================================================
SECTION 7 — EXISTING PROVIDER INTEGRATION
======================================================================

Inspect the existing provider/model/agent architecture.

Identify where provider credentials are currently resolved.

Integrate ISecretProvider at the appropriate boundary.

Possible examples:

- OpenRouter API key
- AI provider API keys
- Hermes credentials
- external agent tokens

Do not refactor unrelated model routing logic.

The goal is:

Provider configuration -> Secret reference -> ISecretProvider -> actual secret -> provider request.

Requirements:

- Secret resolution happens only where needed.
- Secret values are not stored in domain entities.
- Secret values are not persisted in audit records.
- Secret values are not included in exception messages.
- Provider failures caused by missing secrets must classify safely.

If a secret is missing:

- Fail clearly.
- Use a safe exception.
- Allow the existing GlobalExceptionMiddleware / FailureClassifier pipeline to handle it.
- Do not expose secret values or provider internals.

======================================================================
SECTION 8 — CONFIGURATION VALIDATION
======================================================================

Create centralized startup validation.

All production-critical options must be validated before the application starts accepting requests or worker jobs.

Use existing validation patterns where possible.

Do not create a second parallel validation framework.

At minimum validate:

1. Database configuration
   - Required when database-backed features are enabled.
   - Connection string presence.
   - Do not test database connectivity during basic option validation unless existing architecture already does.

2. Security configuration
   - Reuse SecurityOptions.Validate().
   - Validate access key configuration when RequireAccessKey is enabled.
   - Ensure secret references are present when required.

3. Secret management
   - Provider names valid.
   - Required secret references not empty.
   - Production fail-fast behavior valid.

4. Provider configuration
   - Provider ID not empty.
   - Required secret reference configured.
   - Invalid combinations rejected.

5. Resilience configuration
   - Reuse existing validation.

Validation failures must:

- occur at startup where appropriate,
- fail safely,
- never include secret values,
- provide actionable but non-sensitive messages.

======================================================================
SECTION 9 — ENVIRONMENT-SPECIFIC BEHAVIOR
======================================================================

Support explicit environment-aware behavior.

Development:

- Developer-friendly.
- Local secrets may be supported.
- Environment variables should override normal configuration.
- Failures should remain safe.

Production:

- No secrets in appsettings files.
- Required secrets must be externally supplied.
- Missing critical secrets must fail fast.
- Unsafe fallback behavior must be disabled.

Testing:

- Tests must not depend on developer machine secrets.
- Tests must inject fake/test secret providers.
- Tests must not require real credentials.

Do not hardcode environment names outside the standard ASP.NET environment abstractions unless necessary.

======================================================================
SECTION 10 — SECRET REDACTION IN OBSERVABILITY
======================================================================

Phase 17.4.5 already introduced SensitiveDataSanitizer.

Inspect it carefully.

Extend it only if necessary.

Ensure the following cannot leak secrets:

1. Structured logs.
2. AuditLogger.
3. ApiExceptionMapper.
4. ProblemDetails.
5. Diagnostics endpoints.
6. Health checks.
7. Configuration diagnostics.
8. Provider exceptions.
9. Startup validation errors.

Potential secret patterns include:

- Bearer tokens.
- API keys.
- passwords.
- connection strings.
- secret-like environment values.

Do not aggressively redact normal operational identifiers unless they are secrets.

Secret references/names may be visible if they do not reveal the value, but avoid exposing sensitive internal information unnecessarily.

======================================================================
SECTION 11 — CONFIGURATION DIAGNOSTICS
======================================================================

Add safe configuration diagnostics.

Suggested endpoint:

GET /api/operations/configuration

This endpoint must NEVER return:

- secret values
- connection strings
- passwords
- tokens
- API keys

Instead return safe metadata.

Suggested response:

ConfigurationDiagnosticSnapshot

Containing categories such as:

- Environment
- Configuration sources enabled
- Secret provider name
- Secret provider configured
- Required configuration valid
- Database configured
- External providers configured
- Operational security enabled
- Resilience configured

For secret-backed settings, expose only:

- configured/not configured
- secret reference present/not present
- resolved/not resolved only if resolution itself is safe

Never return actual values.

This endpoint should be protected by the existing operational endpoint security mechanism.

Do not bypass Phase 17.4.5 security middleware.

======================================================================
SECTION 12 — HEALTH CHECKS
======================================================================

Add a configuration/secret readiness health check only where useful.

Suggested:

ConfigurationHealthCheck

Requirements:

- Does not expose secret values.
- Reports Healthy when required configuration is valid.
- Reports Unhealthy when critical production configuration is missing.
- Does not perform expensive external secret-provider calls on every health request unless the provider architecture requires it.
- Avoid turning readiness probes into expensive provider diagnostics.

Integrate correctly with:

- /health
- /health/live
- /health/ready

Follow the existing health check conventions.

Do not change liveness semantics unnecessarily.

======================================================================
SECTION 13 — SECRET ROTATION COMPATIBILITY
======================================================================

The architecture must support secret rotation.

Do not implement a cloud-specific secret rotation system.

Instead ensure:

1. Secrets are not permanently copied into singleton configuration objects unnecessarily.
2. Provider clients can resolve fresh credentials when needed.
3. The abstraction does not prevent future providers from supporting dynamic refresh.
4. Secret values are not stored in long-lived diagnostics objects.

If caching is introduced:

- Make it explicit.
- Make expiration configurable.
- Never expose cached secret values.

Do not add caching unless justified.

======================================================================
SECTION 14 — API / WORKER CONSISTENCY
======================================================================

Both composition roots must support the configuration architecture.

Update:

- AI.Factory.Api Program.cs
- AI.Factory.Worker Program.cs

Requirements:

API:

- Register secret providers.
- Register configuration diagnostics.
- Register readiness health checks.
- Expose safe diagnostics endpoint.

Worker:

- Register the same required secret/configuration abstractions.
- Validate critical configuration before processing jobs.
- Do not require HTTP-specific dependencies.

Do not make Worker depend on ASP.NET HttpContext.

======================================================================
SECTION 15 — AUDIT AND METRICS
======================================================================

Reuse the existing observability architecture.

Do not create another metrics system.

If configuration or secret failures are operationally important:

- Add audit events only where meaningful.
- Do not log secrets.
- Do not audit every secret resolution if it would create noise.

Suggested meaningful events:

- ConfigurationValidationFailed
- SecretResolutionFailed
- RequiredSecretMissing

Only add enum members if they provide genuine value and fit the existing audit model.

Metrics may include:

- ConfigurationValidationFailures
- SecretResolutionFailures
- RequiredSecretMissing

Do not create excessive counters.

======================================================================
SECTION 16 — DATABASE SAFETY
======================================================================

Secrets must never be persisted.

Verify that no secret-related value is stored in:

- AuditLogEntry
- metadata JSON
- Project
- WorkItem
- OrchestrationRun
- configuration tables if any exist

No database migration should be created unless an actual non-secret configuration metadata requirement justifies it.

Do not create a Secret table containing secret values.

======================================================================
SECTION 17 — TESTING
======================================================================

Create comprehensive automated tests.

Follow existing test conventions.

Do not require real secrets.

At minimum test:

### Secret Provider

1. Environment variable resolves secret.
2. Missing secret returns safe missing result.
3. Secret value is not logged.
4. Secret provider cancellation behavior where applicable.
5. Composite resolution order if implemented.

### Configuration Validation

6. Missing required production secret configuration fails validation.
7. Valid configuration passes.
8. Invalid provider configuration fails safely.
9. Error messages contain no secret values.
10. Existing SecurityOptions validation still works.

### Sanitization

11. Secret value is removed from:
    - logs where testable,
    - ProblemDetails,
    - audit messages,
    - metadata.

### Provider Integration

12. Provider receives resolved secret.
13. Provider does not receive secret reference instead of value.
14. Missing provider secret produces safe failure.
15. Secret does not appear in exception message.

### Configuration Diagnostics

16. Endpoint returns configuration status.
17. Endpoint is protected by operational endpoint security.
18. Response contains no secret values.
19. Connection strings are not exposed.
20. Secret references may be represented safely.

### Health Checks

21. Valid required configuration -> Healthy.
22. Missing critical configuration -> Unhealthy.
23. Liveness remains unaffected by readiness configuration failures unless existing architecture dictates otherwise.

### API/Worker DI

24. API composition root resolves all new abstractions.
25. Worker composition root resolves all required abstractions.
26. Tests use fake secret providers and do not depend on developer environment.

Run:

dotnet build apps/AI.Factory.slnx

Then:

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Fix all failures caused by this phase.

Do not suppress or skip tests.

======================================================================
SECTION 18 — DOCUMENTATION
======================================================================

Create:

.ai/context/phase-17.4.6-configuration-secret-management.md

Document:

1. Configuration architecture.
2. Configuration precedence.
3. Typed options.
4. Startup validation.
5. Secret provider abstraction.
6. Supported secret providers.
7. Development behavior.
8. Production behavior.
9. Secret reference convention.
10. Secret rotation compatibility.
11. Safe diagnostics.
12. Health check behavior.
13. API and Worker wiring.
14. Security guarantees.
15. Extension points for future cloud secret providers.

Also document explicitly:

- What is intentionally NOT implemented.
- Which secret providers are future extension points.
- Why secrets are never persisted.
- How future OpenViking/provider integrations should obtain credentials.

======================================================================
SECTION 19 — IMPLEMENTATION BOUNDARIES
======================================================================

Do NOT:

- store secrets in SQLite.
- create a secrets table.
- log secrets.
- expose secrets through diagnostics.
- expose connection strings.
- expose raw configuration values.
- introduce a cloud-specific dependency without justification.
- add MediatR.
- add CQRS.
- redesign the provider/model routing system.
- break existing API contracts unnecessarily.
- weaken Phase 17.4.5 security protections.
- bypass the existing SensitiveDataSanitizer.
- create duplicate option abstractions.
- silently fall back to insecure configuration in production.

Prefer:

- small additive changes,
- existing abstractions,
- typed options,
- standard .NET configuration providers,
- dependency injection,
- safe defaults,
- explicit validation,
- fake providers in tests.

======================================================================
FINAL REPORT
======================================================================

After implementation, provide a concise but complete report containing:

1. Summary of changes.
2. Configuration inventory discovered.
3. New abstractions.
4. Existing abstractions reused.
5. Secret resolution flow.
6. Configuration precedence.
7. Startup validation behavior.
8. API/Worker integration.
9. Diagnostics endpoint behavior.
10. Health check behavior.
11. Security guarantees.
12. Files created.
13. Files modified.
14. Database migrations, if any.
15. Test results.
16. Build result.
17. Any real bugs discovered and fixed.
18. Intentionally deferred items.
19. Recommended next phase.

Do not claim completion unless the solution builds and the full test suite passes.