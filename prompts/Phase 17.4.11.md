# Phase 17.4.11 — Production Findings Remediation, Operational Correctness & Final Readiness Closure

You are continuing development of the existing AI Factory platform.

The platform has completed Phase 17.4.1 through Phase 17.4.10, including:

- Global exception handling and RFC ProblemDetails
- Retry, timeout and resilience
- Circuit breakers and bulkhead isolation
- Graceful shutdown and cancellation propagation
- Security hardening
- Configuration and secret management
- Database resilience and optimistic concurrency
- Production Docker/container readiness
- Load, stress and failure testing
- Production operational runbook

The system currently has 597 passing tests.

Phase 17.4.9 intentionally surfaced real production-readiness findings that must now be corrected.

This phase is NOT about adding unrelated features.

The goal is to close known production correctness gaps, eliminate documentation/API drift, improve worker degradation observability, ensure correct database conflict semantics, and produce a final production-readiness closure report.

Do not redesign working architecture unnecessarily.

Preserve Clean Architecture.

Do not introduce duplicate abstractions where existing ones already solve the problem.

Before implementing changes:

1. Inspect the current solution architecture.
2. Inspect existing resilience, worker lifecycle, health check, metrics, database resilience, API exception mapping and operational diagnostics implementations.
3. Inspect all Phase 17.4.x context documents.
4. Inspect the existing test suite.
5. Reuse existing abstractions whenever possible.

==================================================
PRIMARY FINDINGS TO REMEDIATE
==================================================

The following findings were discovered in Phase 17.4.9 and must be addressed.

--------------------------------------------------
FINDING 1 — Worker degradation is not accurately reflected
--------------------------------------------------

Current finding:

OrchestrationRunWorker's polling loop does not update:

- IWorkerHealthCoordinator
- worker lifecycle state
- operational metrics

when transient dependency failures occur.

As a result, the worker may remain reported as Healthy/Ready while repeatedly experiencing dependency failures.

Required outcome:

The worker must accurately transition through:

Starting
    ->
Ready
    ->
Degraded
    ->
Ready (automatic recovery)

and:

Starting / Ready / Degraded
    ->
Stopping

and unrecoverable failures must transition to:

Faulted

Requirements:

1. Inspect the existing IWorkerHealthCoordinator and WorkerHealthCoordinator implementation.

2. Reuse the existing lifecycle abstraction rather than creating a parallel health state system.

3. Add explicit worker lifecycle transitions for transient failures.

4. Define a configurable degradation policy.

Create or extend strongly typed options, for example:

WorkerHealthOptions

with configurable values such as:

ConsecutiveFailureThreshold
RecoverySuccessThreshold
DegradedRetryDelay
FailureWindow

Use reasonable production-safe defaults.

5. Worker behavior:

- A single transient failure must not immediately mark the worker Degraded.
- Consecutive transient failures reaching the configured threshold must transition worker to Degraded.
- Successful polling/execution must reset or reduce the degradation state according to the defined policy.
- Recovery must transition the worker back to Ready automatically.
- Unrecoverable worker host failures must transition to Faulted.

6. Health files must remain consistent:

worker.live
worker.ready
worker.degraded

Expected semantics:

Starting:
    worker.live exists
    worker.ready absent
    worker.degraded absent

Ready:
    worker.live exists
    worker.ready exists
    worker.degraded absent

Degraded:
    worker.live exists
    worker.ready absent
    worker.degraded exists

Stopping:
    worker.live may remain until shutdown finalization
    worker.ready absent

Faulted:
    worker.ready absent
    worker.live absent
    worker.degraded may be removed

Do not leave stale files behind.

7. Operational metrics must reflect:

WorkerDegraded
WorkerRecovered
WorkerPollFailure
WorkerPollSuccess

Reuse existing metric patterns and naming conventions where possible.

8. Audit events must be emitted for meaningful lifecycle transitions:

WorkerDegraded
WorkerRecovered
WorkerFaulted

Do not generate noisy audit events for every successful poll.

9. Worker logs must contain structured context including:

Worker lifecycle state
Failure classification
Correlation ID when available
Consecutive failure count

Do not log secrets.

--------------------------------------------------
FINDING 2 — Unique database constraint violations return HTTP 500
--------------------------------------------------

Current finding:

Concurrent duplicate project creation correctly prevents duplicate rows at the database level.

However, the losing request throws DbUpdateException and is currently mapped to HTTP 500.

Required outcome:

Expected unique constraint violations must return:

HTTP 409 Conflict

with RFC ProblemDetails.

Requirements:

1. Inspect the existing:

ApiExceptionMapper
FailureClassifier
DatabaseResiliencePolicy
ConcurrencyConflictException
ConflictException

2. Do NOT blindly map every DbUpdateException to 409.

Only known uniqueness/constraint conflicts that represent expected client conflicts may map to HTTP 409.

Other database failures must remain sanitized and follow existing infrastructure/database error behavior.

3. Implement provider-aware or classifier-based unique constraint detection.

For SQLite, detect appropriate unique/constraint result codes.

The implementation must not expose:

SQL statements
connection strings
database paths
provider exception internals

4. Preferred architecture:

Introduce or extend an existing failure classification mechanism.

Possible safe abstraction:

DatabaseFailureClassification.UniqueConstraintViolation

or equivalent.

Do not create duplicate exception classification systems.

5. ProblemDetails response:

HTTP 409 Conflict

Title:
"Operation conflict"

Error code:
"conflict"

or a more specific existing contract-compatible code such as:

"duplicate_resource"

only if this does not unnecessarily break existing clients.

Safe detail example:

"A resource with the same unique value already exists."

6. Ensure the correlation ID remains present.

7. Add tests covering:

- Concurrent duplicate project creation.
- SQLite unique constraint violation.
- Generic DbUpdateException remains sanitized and does not incorrectly map to 409.
- Unknown database exception behavior remains unchanged.
- No SQL/provider internals appear in ProblemDetails.

--------------------------------------------------
FINDING 3 — Operational Runbook / API Endpoint Drift
--------------------------------------------------

Inspect the Phase 17.4.10 Production Operational Runbook against the actual implementation.

The Phase 17.4.10 text refers to several diagnostic paths that may differ from the actual API implementation.

Known historical endpoints include paths such as:

GET /api/operations/resilience
GET /api/operations/database
GET /api/operations/configuration
GET /api/operations/deployment

The runbook examples may instead reference paths such as:

/api/operations/diagnostics/database
/api/operations/diagnostics/configuration
/api/operations/diagnostics/deployment

Required outcome:

There must be one canonical operational diagnostics API contract.

Requirements:

1. Inspect the actual API routes.

2. Identify all endpoint/documentation drift.

3. Choose one of these approaches:

Option A — Update documentation to match actual API routes.

Option B — Introduce backward-compatible aliases if there is a real operational compatibility requirement.

Prefer Option A unless aliases provide meaningful compatibility value.

Do not create unnecessary duplicate APIs.

4. Verify all operational endpoints are consistently:

- Protected by operational endpoint security.
- Rate limited.
- Included in OpenAPI policy as appropriate.
- Correlation-ID aware.
- Documented consistently.

5. Update the runbook commands to use the actual canonical routes.

6. Ensure the runbook contains no markdown URLs that are misleading for PowerShell or curl commands.

Use the actual raw endpoint syntax inside code blocks.

7. Add automated route contract tests where practical.

--------------------------------------------------
FINDING 4 — Worker Health Semantics Must Match Runbook
--------------------------------------------------

The operational runbook describes:

worker.live
worker.ready
worker.degraded

Verify these semantics against actual implementation.

Required outcome:

The implementation, health checks, Docker HEALTHCHECK behavior, and runbook must describe the same lifecycle model.

Requirements:

1. Inspect Dockerfile.worker.

2. Inspect WorkerHealthCoordinator.

3. Inspect WorkerHealthLifecycleHostedService.

4. Inspect Worker health tests.

5. Determine whether Docker HEALTHCHECK currently checks the correct state.

Important distinction:

A Degraded worker should usually remain alive.

Do not automatically treat every Degraded state as a dead process.

Define and document:

Liveness:
"Is the worker process alive?"

Readiness:
"Is the worker currently able to accept and claim new work?"

Degraded:
"Is the worker alive but experiencing sustained dependency failures?"

The Docker health check behavior must be intentional and documented.

If Docker has only one health check, define which semantic it represents.

Preferred principle:

Docker/container health should generally represent liveness unless the deployment orchestrator explicitly requires readiness semantics.

Do not make a transient dependency outage restart an otherwise healthy worker in a restart loop.

--------------------------------------------------
FINDING 5 — Circuit Breaker Testability and Time Abstraction
--------------------------------------------------

Phase 17.4.9 documented a limitation:

Circuit breaker tests use short real delays because there is no controllable time abstraction.

Evaluate whether the current circuit breaker architecture should be improved with a time abstraction.

Requirements:

1. Inspect existing circuit breaker implementation.

2. Determine whether System.TimeProvider or another existing .NET-native time abstraction can be integrated without unnecessary redesign.

3. If practical:

- Inject TimeProvider into circuit breaker infrastructure.
- Use it for break duration calculations.
- Preserve thread safety.
- Update tests to use FakeTimeProvider or equivalent.

4. Remove unnecessary Thread.Sleep-based tests where a deterministic clock can be used.

5. Do not introduce a custom clock abstraction if TimeProvider is sufficient.

6. Ensure production behavior remains unchanged.

7. If integration is not practical without major architectural churn:

- Do not force the change.
- Document the technical reason.
- Improve existing tests as much as possible.

--------------------------------------------------
FINDING 6 — SQLite Busy/Locked Testing Limitations
--------------------------------------------------

Phase 17.4.9 documented that the real SQLite busy exception path could not always be reproduced because the provider's busy timeout absorbed contention.

Improve confidence without creating flaky tests.

Requirements:

1. Preserve the existing classifier-level test.

2. Inspect whether SQLite connection string configuration exposes busy timeout settings.

3. If safe and deterministic:

Create a dedicated integration test configuration that reduces or controls busy timeout so SQLITE_BUSY / SQLITE_LOCKED can be reliably reproduced.

4. Do not weaken production busy timeout configuration only to satisfy tests.

5. Test configuration must be isolated from production.

6. If deterministic reproduction remains impractical:

Document:

- why the provider absorbs the exception,
- what layer is tested directly,
- what integration behavior remains covered.

--------------------------------------------------
FINDING 7 — Production Runbook Safety Improvements
--------------------------------------------------

Review the operational runbook for unsafe or incomplete production instructions.

Add explicit procedures for:

A. Backup

Define supported SQLite backup strategy.

Requirements:

- Never copy a live SQLite database file blindly while writes are occurring.
- Use a consistent backup mechanism.
- Define backup location outside the primary data volume.
- Include retention guidance.
- Document backup verification.

B. Restore

Define restore procedure including:

1. Stop services.
2. Preserve failed database copy.
3. Restore backup.
4. Verify file ownership.
5. Start migrator.
6. Start services.
7. Verify readiness.
8. Verify representative data.

C. Disaster Recovery

Define:

RPO
RTO assumptions

These may be configurable deployment targets rather than guarantees.

Clearly distinguish:

application capability
operator responsibility
infrastructure responsibility

D. Secret Rotation

Document safe rotation for:

OPENROUTER_API_KEY
OPERATIONAL_ACCESS_KEY
database credentials when a non-SQLite provider is used

Explain rolling/restart requirements.

E. Migration Rollback Policy

Clarify:

- EF migrations are forward migrations by default.
- Do not blindly downgrade production databases.
- Failed migrations require investigation.
- Rollback should use tested release/database restore procedures.

--------------------------------------------------
FINAL PRODUCTION READINESS GAP REVIEW
--------------------------------------------------

After implementing the remediations, perform a final review against the following categories.

1. API Reliability
- Global exception handling
- ProblemDetails consistency
- Correlation IDs
- Retry/timeout behavior
- Circuit breaker behavior
- Bulkhead behavior
- Correct HTTP conflict mapping

2. Worker Reliability
- Graceful shutdown
- Cancellation propagation
- Degraded state transitions
- Automatic recovery
- Faulted state
- Lease recovery
- No double execution

3. Database Reliability
- Retry behavior
- SQLite busy/locked behavior
- Optimistic concurrency
- Unique constraints
- Transactions
- Idempotency

4. Security
- Security headers
- Rate limits
- Request size limits
- Input validation
- Operational endpoint access
- Secret redaction
- Configuration validation

5. Configuration & Secrets
- Fail-fast validation
- No secrets in appsettings
- Environment secret resolution
- Sanitized diagnostics

6. Containers & Deployment
- Non-root containers
- Multi-stage builds
- Persistent volumes
- Migration ordering
- Health semantics
- Graceful shutdown
- Restart safety

7. Observability
- Metrics
- Audit events
- Structured logs
- Correlation IDs
- Operational diagnostics

8. Operational Readiness
- Start
- Stop
- Restart
- Failure diagnosis
- Backup
- Restore
- Disaster recovery
- Secret rotation
- Migration failure procedures

For each category classify the result as:

Ready
Ready with documented limitation
Not ready

Do not falsely mark something Ready if a known limitation remains.

==================================================
TESTING REQUIREMENTS
==================================================

Add focused tests for every remediation.

At minimum:

Worker health:

- Single transient failure does not degrade worker.
- Consecutive failures reach degradation threshold.
- Degraded state creates correct files.
- Recovery returns to Ready.
- Metrics are recorded.
- Audit event emitted only on state transition.
- Faulted state removes liveness/readiness files correctly.
- No stale health files remain after transitions.

Database conflict:

- SQLite unique constraint maps to HTTP 409.
- Concurrent duplicate project creation maps losing request to 409.
- Generic DbUpdateException remains non-conflict.
- No provider internals leak.

Operational contract:

- Every documented diagnostic endpoint exists.
- Runbook endpoint names match canonical API routes.
- Operational endpoint security still applies.
- Rate limiting still applies.

Circuit breaker:

- Deterministic break duration testing if TimeProvider is adopted.
- Half-open behavior remains correct.
- No regression in concurrent safety.

Database busy:

- Preserve classifier-level tests.
- Add deterministic integration coverage only if safe.

Run:

dotnet build apps/AI.Factory.slnx

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Run relevant category filters if they exist:

dotnet test apps/AI.Factory.slnx --filter "Category=Stress"

dotnet test apps/AI.Factory.slnx --filter "Category=Load"

dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"

Also validate:

docker compose config

docker compose build

Perform live smoke testing if the environment supports Docker.

==================================================
DOCUMENTATION
==================================================

Create:

.ai/context/phase-17.4.11-production-findings-remediation.md

The document must include:

1. Original findings from Phase 17.4.9.
2. Root cause analysis.
3. Changes implemented.
4. Worker lifecycle state machine.
5. Database conflict mapping rules.
6. Canonical operational endpoint contract.
7. Docker health semantics.
8. Circuit breaker time-testing decision.
9. SQLite busy/locked testing decision.
10. Backup and restore procedure.
11. Secret rotation procedure.
12. Migration rollback policy.
13. Remaining limitations.

Update the Phase 17.4.10 Production Operational Runbook so it exactly matches the implementation.

==================================================
FINAL OUTPUT
==================================================

At completion provide a structured walkthrough containing:

# Phase 17.4.11 — Production Findings Remediation & Final Readiness Closure

## Summary

## Findings Resolved

For each original finding:

- Problem
- Root Cause
- Fix
- Tests Added
- Final Status

## Worker Lifecycle Model

## Database Conflict Mapping

## Operational Endpoint Contract

## Container Health Semantics

## Backup & Restore Readiness

## Secret Rotation

## Migration Failure and Rollback Policy

## Circuit Breaker Testability

## SQLite Busy/Locked Coverage

## Final Production Readiness Matrix

Use:

Ready
Ready with documented limitation
Not ready

## Files Created

## Files Modified

## Validation

Include:

- Build result
- Full test result
- Stress/load/resilience test result
- Docker Compose validation
- Docker build result
- Live smoke test result

## Remaining Limitations

Be explicit.

Do not claim zero limitations unless all known limitations are actually resolved.

The implementation must preserve all previously passing behavior while closing the known Phase 17.4.9 and Phase 17.4.10 operational gaps.