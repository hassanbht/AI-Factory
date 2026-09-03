# Phase 17.4.8 — Deployment & Container Production Readiness

You are continuing development of the **AI Factory** platform.

The previous phases of the production hardening program are complete:

- Phase 17.4.1 — Global Exception Handling, Problem Details & API Error Contract
- Phase 17.4.2 — Retry, Timeout & Resilience
- Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience
- Phase 17.4.4 — Graceful Shutdown, Cancellation Propagation & Background Worker Reliability
- Phase 17.4.5 — Security Hardening
- Phase 17.4.6 — Configuration & Secret Management
- Phase 17.4.7 — Database Resilience & Concurrency

The current solution has passed all automated tests.

The goal of this phase is to make the platform **production-ready from a deployment and container lifecycle perspective**.

Do not redesign or duplicate the resilience, security, secret management, database concurrency, or graceful shutdown mechanisms already implemented.

This phase must focus on:

1. Production container images
2. API and Worker deployment topology
3. Health-aware container lifecycle
4. Startup ordering and dependency readiness
5. Environment-based production configuration
6. Non-root container execution
7. Signal handling and graceful shutdown compatibility
8. Database migration strategy
9. Persistent storage and restart safety
10. Docker Compose production readiness
11. Resource constraints and operational limits
12. Deployment diagnostics and verification
13. Production deployment documentation

Maintain Clean Architecture and preserve the current API contracts.

---

# 1. Primary Architecture Goals

The production deployment topology should explicitly support:

```text
                    ┌───────────────────────┐
                    │      Reverse Proxy     │
                    │   / Load Balancer      │
                    └───────────┬───────────┘
                                │
                                ▼
                       ┌────────────────┐
                       │ AI.Factory.Api │
                       │                │
                       │ Stateless API  │
                       └───────┬────────┘
                               │
                               │
                    ┌──────────▼──────────┐
                    │      Database        │
                    │ SQLite initially /   │
                    │ provider abstraction │
                    └──────────┬──────────┘
                               │
                               │
                       ┌───────▼────────┐
                       │AI.Factory.Worker│
                       │                 │
                       │ Background Jobs │
                       │ Orchestration   │
                       └─────────────────┘
```

The deployment design must ensure:

- API containers can restart safely.
- Worker containers can restart safely.
- Container restarts do not corrupt database state.
- Database state survives container recreation.
- Graceful shutdown mechanisms already implemented receive the correct process signals.
- Health checks distinguish startup, liveness, and readiness.
- Startup failures are fail-fast and observable.
- Secrets are injected from environment/runtime secret providers rather than baked into images.
- Containers run as non-root.
- Production images are minimal and deterministic.

---

# 2. Production Dockerfile Strategy

Review the existing Dockerfiles.

Create or improve production-grade Dockerfiles for:

```text
AI.Factory.Api
AI.Factory.Worker
```

Use multi-stage builds.

The required stages should conceptually include:

```text
restore
build
test/build validation where appropriate
publish
runtime
```

Requirements:

- SDK image must not be used as the final runtime image.
- Final images should contain only runtime dependencies.
- Use `dotnet publish`.
- Use Release configuration.
- Disable unnecessary development tooling.
- Do not include source code in the final runtime image.
- Do not include test projects in runtime images.
- Do not bake secrets into images.
- Do not bake development configuration into images.
- Configure production-safe environment defaults.
- Run as a dedicated non-root user.
- Use explicit working directories.
- Define explicit entrypoints.

Verify compatibility with the current .NET version used by the solution.

---

# 3. Non-Root Execution

Containers must not run as root.

Implement a dedicated runtime user.

Requirements:

- API and Worker containers execute as non-root.
- Application directories have correct ownership.
- Writable directories are explicitly identified.
- Database volume permissions are compatible with the runtime user.
- Temporary directories required by the application are writable.
- Container startup does not require privileged execution.

Add automated verification where practical.

---

# 4. Container Health Model

The existing application already exposes health endpoints.

Review and standardize the production health model.

The platform must clearly distinguish:

```text
/health/live
/health/ready
```

Expected semantics:

### Liveness

The application process is alive.

Liveness should generally not fail because:

- Database is temporarily unavailable.
- External provider is unavailable.
- A circuit breaker is open.

The purpose is to determine whether the process itself is alive.

### Readiness

The application is ready to receive production traffic or perform work.

Readiness may depend on:

- Valid configuration
- Secret provider readiness
- Database connectivity
- Required startup validation
- Critical dependency state where appropriate

Do not overload liveness with readiness semantics.

---

# 5. Docker Health Checks

Configure Docker-level health checks for API and Worker.

Requirements:

### API

Use an application health endpoint.

The health check must:

- Have a reasonable interval.
- Have a timeout.
- Have retries.
- Have a startup period.

Avoid aggressive polling.

### Worker

Determine the correct health strategy for a background worker.

The Worker must expose or otherwise provide a reliable health signal indicating:

- Process is running.
- Startup completed.
- Critical initialization succeeded.
- Graceful shutdown state can be distinguished where useful.

Do not falsely report healthy while startup validation has failed.

---

# 6. Startup Dependency Ordering

Do not rely exclusively on Docker Compose `depends_on` for application correctness.

The application must continue using its own:

- Configuration validation
- Secret validation
- Database resilience
- Retry policies
- Readiness checks

However, Docker Compose should improve startup ergonomics.

Implement appropriate startup dependency conditions.

Example intent:

```text
Database starts
      ↓
Database becomes healthy/available
      ↓
API/Worker start
      ↓
Application validates configuration
      ↓
Application becomes ready
```

Important:

A container being started is not equivalent to the dependency being ready.

Avoid infinite startup loops.

---

# 7. Database Persistence Strategy

Review how SQLite is currently deployed.

The database must not live only inside an ephemeral writable container layer.

Implement persistent storage using a named volume or explicit bind mount strategy.

Example conceptual structure:

```text
/var/lib/ai-factory/data
```

Requirements:

- Database survives container recreation.
- Database directory permissions support the non-root runtime user.
- SQLite WAL/journal files are persisted correctly.
- Backup/migration procedures are not broken by container recreation.
- API and Worker share the database safely according to the existing concurrency architecture.

Document the limitations of SQLite in multi-container and multi-replica deployments.

Do not pretend SQLite is suitable for arbitrary horizontal scaling.

The architecture should make the future migration to another relational provider possible without redesigning the deployment model.

---

# 8. Database Migration Strategy

Define a production-safe migration strategy.

Do not automatically allow multiple API/Worker replicas to race while applying EF Core migrations.

Choose and implement a clear strategy.

Preferred options may include:

### Option A — Dedicated migration container/job

```text
migration job
      ↓
database migration completes
      ↓
API and Worker become available
```

### Option B — Explicit startup migration service with distributed safety guarantees

Only choose this if it can be safely implemented.

The preferred design for production is:

> A dedicated migration execution step/container rather than uncontrolled migrations performed independently by every API or Worker instance.

Requirements:

- Migration failure must fail visibly.
- Migration success must be observable.
- Migration container must exit successfully when complete.
- API/Worker startup must not silently continue against an incompatible schema.
- Secrets and connection strings must continue using the existing configuration/secret architecture.
- No credentials may be logged.

---

# 9. Docker Compose Production Configuration

Create or improve production-oriented Docker Compose configuration.

Do not make development conveniences the production default.

The Compose topology should clearly define services such as:

```text
database storage
migration
api
worker
```

depending on the actual architecture.

Configure:

- Named persistent volumes.
- Environment-based configuration.
- Secret injection through environment variables or runtime secret mechanisms compatible with Phase 17.4.6.
- Health checks.
- Restart policies.
- Resource limits where supported.
- Service dependency conditions.
- Explicit networks where useful.

Avoid:

- Hard-coded secrets.
- Development connection strings.
- Source code mounts.
- Hot reload.
- Debugger ports.
- Unnecessary privileged capabilities.

---

# 10. Environment Profiles

Ensure the application supports appropriate environments such as:

```text
Development
Staging
Production
```

Requirements:

- `appsettings.json` contains safe defaults only.
- Environment-specific configuration does not contain production credentials.
- Production secrets come from the configured secret provider/environment.
- Development-only behavior is not enabled automatically in production.
- Swagger/OpenAPI exposure follows the existing security policy.
- Diagnostic endpoints remain protected according to Phase 17.4.5 and Phase 17.4.6.

Review:

```text
ASPNETCORE_ENVIRONMENT
DOTNET_ENVIRONMENT
```

Ensure API and Worker behavior is consistent.

---

# 11. Graceful Shutdown and Container Signals

Phase 17.4.4 already implemented graceful shutdown and cancellation propagation.

This phase must verify container integration with that behavior.

Requirements:

- Docker must deliver SIGTERM directly to the application process.
- Avoid shell entrypoints that swallow signals.
- Ensure the Worker receives cancellation correctly.
- Configure an appropriate container stop grace period.
- Ensure in-flight operations use the existing cancellation mechanisms.
- Readiness should transition appropriately during shutdown.
- New work should stop being accepted before process termination.
- Forced termination should only occur after the configured grace period.

Add integration tests where practical.

---

# 12. Resource Limits

Introduce configurable production resource guidance.

Review CPU and memory usage characteristics of:

- API
- Worker
- Database
- Build/test execution
- External provider workloads

Docker Compose configuration should support resource constraints.

Examples:

```text
CPU limits
Memory limits
Memory reservations
```

Do not choose arbitrary extremely low limits that would make the system unstable.

Document recommended defaults separately from mandatory runtime constraints.

Ensure:

- Bulkheads continue to work correctly under constrained resources.
- Shutdown does not fail because memory is exhausted.
- Diagnostics remain available under normal degraded conditions.

---

# 13. Filesystem Hardening

Review container filesystem requirements.

Where compatible with the application:

- Prefer read-only root filesystems.
- Explicitly mount writable directories.
- Use temporary filesystems where appropriate.
- Avoid unnecessary writable paths.

Do not break:

- SQLite persistence.
- Application logs.
- Temporary build/test artifacts where required.
- Worker execution.

If read-only root filesystem cannot be enabled universally, document the exact reason and isolate writable paths.

---

# 14. Logging & Container Observability

Production containers should log to standard output/error.

Review existing logging configuration.

Requirements:

- Structured logging remains intact.
- Correlation IDs remain available.
- Sensitive data redaction from Phase 17.4.5 remains active.
- Logs must not depend exclusively on files inside ephemeral containers.
- Startup configuration validation failures are clearly observable.
- Container lifecycle events are observable.

Avoid duplicate logging pipelines.

Do not introduce secrets into logs.

---

# 15. Deployment Diagnostics

Extend safe operational diagnostics where useful.

Possible additions:

```text
GET /api/operations/deployment
```

If implemented, diagnostics may expose:

- Environment
- Application version
- Build/version metadata
- Process architecture
- Runtime version
- Containerized execution detection
- Startup completion state
- Database provider name
- Health state summaries

Never expose:

- Connection strings
- API keys
- Tokens
- Passwords
- Secret values
- Internal filesystem paths that are unnecessary for diagnostics

The endpoint must continue using existing operational endpoint security and rate limiting.

Do not duplicate the configuration diagnostics already implemented in Phase 17.4.6.

---

# 16. Application Versioning

Introduce deterministic application version metadata.

Support deployment diagnostics containing:

```text
ApplicationName
Version
InformationalVersion
Environment
BuildTimestamp (if safely available)
GitCommit (if safely injected during build)
```

Requirements:

- Version metadata must not require secrets.
- Build metadata should be deterministic.
- Runtime diagnostics must not require access to `.git`.
- Docker builds may inject build arguments where appropriate.

Avoid exposing internal CI credentials or build system tokens.

---

# 17. Container Restart Safety

Verify behavior under:

```text
API restart
Worker restart
Database temporary unavailability
Container SIGTERM
Container restart during active orchestration
Worker restart during queued operation
```

The existing resilience and persistence mechanisms must remain compatible.

Verify:

- No duplicate orchestration execution caused by restart.
- Idempotency mechanisms continue working.
- Transactions do not leave partially committed state.
- Optimistic concurrency behavior remains correct.
- Worker cancellation is persisted/handled safely according to the existing orchestration model.

Do not introduce a second duplicate-execution system if one already exists.

Reuse existing abstractions.

---

# 18. API Deployment Readiness

Verify that API startup proceeds through:

```text
Process start
   ↓
Configuration binding
   ↓
Secret provider initialization
   ↓
Options validation
   ↓
Database configuration
   ↓
Service composition
   ↓
HTTP server start
   ↓
Readiness becomes healthy
```

Failure at a critical stage must:

- Fail clearly.
- Produce safe logs.
- Avoid partial readiness.

---

# 19. Worker Deployment Readiness

Verify Worker startup separately.

Expected lifecycle:

```text
Process start
   ↓
Configuration binding
   ↓
Secret provider initialization
   ↓
Options validation
   ↓
Database configuration
   ↓
Worker initialization
   ↓
Background processing readiness
   ↓
Worker healthy
```

The Worker must not silently continue if critical initialization fails.

---

# 20. Automated Tests

Add tests covering the production deployment concerns.

Suggested test groups:

```text
ContainerConfigurationTests.cs
DockerfileValidationTests.cs
DeploymentConfigurationTests.cs
HealthEndpointDeploymentTests.cs
MigrationStartupTests.cs
EnvironmentConfigurationTests.cs
NonRootContainerTests.cs
GracefulShutdownContainerTests.cs
PersistentStorageTests.cs
DeploymentDiagnosticsTests.cs
```

Test at least:

### Docker / Container

- Runtime image is not SDK image.
- Containers use non-root user.
- Entrypoint does not swallow signals.
- Production images do not contain development configuration/secrets.
- Expected ports are configured explicitly.

### Health

- Liveness remains healthy during temporary dependency failures where appropriate.
- Readiness becomes unhealthy when critical startup dependencies fail.
- API Docker health check targets the correct endpoint.
- Worker health behavior reflects initialization state.

### Configuration

- Production environment uses secret references rather than embedded secrets.
- Missing required secrets fail safely.
- Production configuration validation fails fast.

### Database

- Persistent volume path is configured correctly.
- Migration process completes before normal service startup.
- Migration failure prevents incompatible application startup.

### Shutdown

- SIGTERM triggers existing graceful shutdown behavior.
- New work is rejected/stopped during shutdown.
- In-flight operations receive cancellation.

### Restart

- Restarting Worker does not cause duplicate persisted execution.
- Restarting API does not corrupt state.
- Existing database concurrency protection remains valid.

Do not make tests dependent on external internet services.

Use local/container-compatible test fixtures where possible.

---

# 21. Documentation

Create:

```text
.ai/context/phase-17.4.8-deployment-container-production-readiness.md
```

Document:

1. Deployment architecture.
2. API container lifecycle.
3. Worker container lifecycle.
4. Health check model.
5. Liveness vs readiness semantics.
6. Persistent storage model.
7. SQLite production limitations.
8. Migration strategy.
9. Environment configuration.
10. Secret injection.
11. Graceful shutdown behavior.
12. Restart safety.
13. Resource guidance.
14. Container security decisions.
15. Production deployment checklist.
16. Rollback considerations.
17. Known limitations and future PostgreSQL migration considerations.

Also add or update deployment documentation suitable for an operator.

---

# 22. Important Architectural Constraints

Do not:

- Move infrastructure concerns into Domain.
- Duplicate retry or circuit breaker implementations.
- Duplicate secret provider abstractions.
- Hard-code production secrets.
- Automatically expose operational diagnostics publicly.
- Break existing ProblemDetails contracts.
- Break correlation IDs.
- Break existing audit logging.
- Log credentials.
- Apply EF migrations independently from every application replica.
- Assume Docker Compose startup ordering guarantees dependency readiness.
- Treat SQLite as horizontally scalable across arbitrary API/Worker replicas.
- Run production containers as root without an explicit unavoidable reason.

Prefer:

```text
Clean Architecture
Explicit configuration
Fail-fast startup
Immutable images
Externalized configuration
Non-root runtime
Health-aware lifecycle
Persistent data
Graceful shutdown
Safe migrations
Observable deployment
```

---

# 23. Required Verification

Run:

```powershell
dotnet build apps/AI.Factory.slnx
```

Then:

```powershell
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

Additionally validate Docker/Compose configuration.

Where Docker is available, run production-oriented verification such as:

```powershell
docker compose -f docker-compose.yml config
```

and build the production images.

If a separate production compose file is introduced, validate it explicitly.

Perform a manual or automated smoke test covering:

```text
Build images
↓
Start persistent database storage
↓
Run migrations
↓
Start API
↓
Start Worker
↓
Verify liveness
↓
Verify readiness
↓
Restart API
↓
Restart Worker
↓
Graceful shutdown
↓
Verify persisted state survives recreation
```

---

# 24. Final Deliverable

When implementation is complete, provide a walkthrough containing:

## Summary

What production deployment capabilities were added.

## Architecture

Explain:

```text
Container topology
Migration flow
Startup flow
Health model
Shutdown flow
Persistent storage
Secret injection
```

## Security

Explain:

- Non-root execution.
- Filesystem hardening.
- Secret handling.
- Diagnostic endpoint protection.

## Reliability

Explain:

- Restart safety.
- Graceful shutdown.
- Database persistence.
- Health-aware recovery.

## Docker / Compose

List:

- Created files.
- Modified files.
- Services.
- Volumes.
- Health checks.
- Restart policies.

## Testing

Provide:

- Build result.
- Full test result.
- Docker configuration validation result.
- Image build result if Docker is available.
- Any integration/container tests executed.

## Known Limitations

Explicitly document any remaining limitations, especially:

- SQLite scaling limitations.
- Compose vs orchestration platform differences.
- Future production orchestration requirements.

Do not claim that functionality is implemented unless it has actually been implemented and verified.

The final implementation must compile successfully and preserve all previously passing tests.