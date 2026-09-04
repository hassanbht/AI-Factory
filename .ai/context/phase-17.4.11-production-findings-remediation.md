# Phase 17.4.11 — Production Findings Remediation

## 1. Original findings (Phase 17.4.9)

1. Worker degradation was not accurately reflected — a struggling worker stayed reported Ready.
2. Unique database constraint violations returned HTTP 500 instead of 409 Conflict.
3. The Phase 17.4.10 runbook documented `/api/operations/diagnostics/{database,configuration,deployment}`
   while the actual routes are `/api/operations/{database,configuration,deployment}`.
4. Docker's worker HEALTHCHECK tested `worker.ready`, so a transient dependency outage that pushed the
   worker into Degraded would fail the container health check and trigger a restart loop.
5. The circuit breaker had no controllable time abstraction; tests relied on short real `Task.Delay`s.
6. SQLite busy/locked (`SQLITE_BUSY`/`SQLITE_LOCKED`) exceptions were hard to reproduce deterministically
   because the provider's own contention handling usually absorbed it before the app-level retry path saw it.
7. The runbook was missing explicit secret rotation and migration rollback procedures.

## 2. Root cause analysis

- **Finding 1**: `OrchestrationRunWorker`'s poll loop caught and logged dependency exceptions but never
  called `IWorkerHealthCoordinator` or `IOperationalMetrics` — the coordinator only had two producers of
  state (`MarkReady`/`MarkFaulted`/`MarkStopping` from `WorkerHealthLifecycleHostedService`, driven by host
  start/stop events), with nothing wired to the actual poll outcome.
- **Finding 2**: `ApiExceptionMapper` mapped every `DbUpdateException`/`DbException` to a single sanitized
  500 branch with no classification step distinguishing an expected unique-constraint conflict from a real
  infrastructure failure.
- **Finding 3**: the Phase 17.4.10 runbook was authored against an earlier or assumed route shape and never
  cross-checked against `apps/src/AI.Factory.Api/Program.cs`'s actual minimal-API route registrations.
- **Finding 4**: the Dockerfile was written checking the "positive" signal file (`worker.ready`) without
  distinguishing liveness from readiness semantics — `worker.ready` is deliberately absent while Degraded,
  which is correct for a load-balancer's routing decision but wrong for a container orchestrator's
  restart decision.
- **Findings 5 & 6**: pre-existing limitations from Phase 17.4.9, carried forward — see §8/§9 below.
- **Finding 7**: the DR runbook already covered backup/restore/RPO/RTO in depth; secret rotation and
  migration rollback were simply never written.

## 3. Changes implemented

### Finding 1 — Worker degradation

- `WorkerHealthOptions.cs` (new): `ConsecutiveFailureThreshold` (default 3), `RecoverySuccessThreshold`
  (default 1), `FailureWindow` (default 5 minutes — a failure more than this long after the previous one
  starts a fresh streak instead of accumulating). Bound from the `WorkerHealth` config section in
  `Worker/Program.cs`, validated at startup.
- `IWorkerHealthCoordinator.cs`: added `WorkerLifecycleState.Degraded`, `WorkerHealthSnapshot.ConsecutiveFailures`,
  and `ReportPollFailure(Exception)` / `ReportPollSuccess()`.
- `WorkerHealthCoordinator.cs`: implements the threshold/recovery/failure-window policy from
  `WorkerHealthOptions`; emits `AuditEventType.WorkerDegraded` / `WorkerRecovered` / `WorkerFaulted` (new
  enum members) through a fire-and-forget scoped `IAuditLogger` resolve, mirroring the existing pattern in
  `CircuitBreakerRegistry.CircuitBreakerImpl.TransitionToUnderLock`; logs carry lifecycle state, failure
  classification (`ex.GetType().Name`), and consecutive-failure count structurally. A `ForTesting(...)`
  static factory replaces the old ad-hoc `int degradationThreshold` constructor so DI keeps a single
  unambiguous constructor.
- `OrchestrationRunWorker.cs`: the poll loop now calls `ReportPollSuccess()`/`ReportPollFailure(ex)` and
  records `WorkerPollFailure`/`WorkerDegraded`/`WorkerRecovered` metrics on the actual transition edges
  (not every tick).
- `IOperationalMetrics.cs`: added `RecordWorkerPollFailure/Degraded/Recovered` counters.

### Finding 2 — Unique constraint → 409

- `ApiExceptionMapper.cs`: added `IsUniqueConstraintViolation(Exception?)`, checking
  `SqliteException.SqliteErrorCode == 19` with extended code 2067 (`SQLITE_CONSTRAINT_UNIQUE`) or 1555
  (`SQLITE_CONSTRAINT_PRIMARYKEY`), or a message-text fallback, walking `InnerException` (EF wraps the
  SQLite exception inside `DbUpdateException`). Matched *before* the generic
  `DbUpdateException or DbException → 500` branch, so only classified unique-constraint conflicts map to
  409 — every other database failure keeps its existing sanitized 500 behavior unchanged.

### Finding 3 — Operational endpoint contract

- Corrected `/api/operations/diagnostics/{database,configuration,deployment}` →
  `/api/operations/{database,configuration,deployment}` in `phase-17.4.10-production-runbook.md`,
  `phase-17.4.10-disaster-recovery-runbook.md`, and `phase-17.4.10-production-readiness-review.md`
  (Option A — fix docs to match the implementation; no alias routes were introduced, since none of the 9
  actual routes had any external consumer depending on the wrong path).
- Added `OperationalEndpointContractTests.cs`, a `[Theory]` over the 7 canonical `/api/operations/*` paths
  asserting each resolves (not 404) and enforces the access key (401 without one) — pins the contract so
  runbook drift is caught by CI going forward.

### Finding 4 — Container health semantics

- `Dockerfile.worker`: `HEALTHCHECK` now tests `worker.live` instead of `worker.ready`, with a comment
  explaining the liveness/readiness distinction. This was the one change in this phase with real
  production-safety impact: previously, 3 consecutive poll failures (a transient dependency blip) would
  both mark the worker Degraded *and* fail the Docker health check, causing Docker to restart an
  otherwise-healthy process — exactly the restart-loop the phase brief said to avoid.
- Runbook updated with a note explaining the same decision (§2.3).

## 4. Worker lifecycle state machine

```
Starting --(host ApplicationStarted)--> Ready
Ready --(N consecutive poll failures >= ConsecutiveFailureThreshold)--> Degraded
Degraded --(M consecutive poll successes >= RecoverySuccessThreshold)--> Ready
{Starting, Ready, Degraded} --(host ApplicationStopping)--> Stopping
{Starting, Ready, Degraded} --(unrecoverable host exception)--> Faulted
```

Health files:

| State    | worker.live | worker.ready | worker.degraded |
|----------|:-----------:|:------------:|:----------------:|
| Starting | yes         | no           | no               |
| Ready    | yes         | yes          | no               |
| Degraded | yes         | no           | yes              |
| Stopping | yes         | no           | no               |
| Faulted  | no          | no           | no               |

No stale files persist across a transition — every transition method removes the files that shouldn't be
present in the new state before/while touching the ones that should (verified by
`WorkerHealthCoordinatorTests` and `WorkerRestartRecoveryTests`, including a worker-restart-after-Stopping
scenario that reinitializes cleanly to Starting).

## 5. Database conflict mapping rules

`DbUpdateException` (and a bare `SqliteException`) is classified via `ApiExceptionMapper.IsUniqueConstraintViolation`
*before* the generic database-error branch:

- SQLite error code 19 (`SQLITE_CONSTRAINT`) with extended code 2067 (`SQLITE_CONSTRAINT_UNIQUE`) or 1555
  (`SQLITE_CONSTRAINT_PRIMARYKEY`), or a `UNIQUE constraint failed` message match → **409 Conflict**,
  `code: "conflict"`, sanitized detail `"A resource with the specified unique field already exists."`
  (`ShouldAudit: false` — an expected client conflict, not an infrastructure event worth auditing).
- Any other `DbUpdateException`/`DbException` → unchanged **500**, `code: "database_error"`, sanitized
  detail, `ShouldAudit: true`.
- Correlation ID and `ProblemDetails.Instance` are preserved identically to every other mapped exception —
  no special-casing needed since the classification happens inside the existing `Map`/`CreateProblemDetails`
  pipeline.
- No SQL text, table/column names, or provider internals appear in the response in either branch
  (`ApiExceptionMapperTests` asserts this explicitly for both a raw `SqliteException` and an EF-wrapped one).

## 6. Operational endpoint contract

Canonical routes (from `apps/src/AI.Factory.Api/Program.cs`), all behind `OperationalEndpointSecurityMiddleware`
+ `RequireRateLimiting("OperationalApi")` + the global `CorrelationMiddleware`, all included in OpenAPI:

```
GET /api/operations/metrics
GET /api/operations/resilience
GET /api/operations/recent
GET /api/operations/{correlationId}
GET /api/projects/{projectId}/diagnostics
GET /api/operations/active
GET /api/operations/configuration
GET /api/operations/database
GET /api/operations/deployment
```

The runbook and DR runbook now reference these exact paths.

## 7. Container health semantics

- **Liveness** ("is the process alive?") — `GET /health/live` (API), `test -f worker.live` (Worker). This is
  what Docker's `HEALTHCHECK` now checks for both services.
- **Readiness** ("can it accept work / traffic right now?") — `GET /health/ready` (API, checks DB
  connectivity), `test -f worker.ready` (Worker). Not wired to the container `HEALTHCHECK` for the worker;
  an orchestrator that needs readiness for routing decisions should poll it separately (documented in the
  runbook), consistent with the phase brief's stated preference ("container health should generally
  represent liveness unless the deployment orchestrator explicitly requires readiness semantics").
- **Degraded** — alive, not ready, sustained dependency failures. Intentionally does **not** fail the
  container `HEALTHCHECK` (Finding 4's fix) — a transient outage must not restart an otherwise-healthy
  worker.

## 8. Circuit breaker testability — deferred

**Not implemented in this pass.** The production circuit breaker is
`CircuitBreakerRegistry.CircuitBreakerImpl` (`apps/src/AI.Factory.Application/Resilience/CircuitBreakerRegistry.cs`) —
registered via `AddSingleton<ICircuitBreakerRegistry, CircuitBreakerRegistry>()` in both `Api/Program.cs` and
`Worker/Program.cs`. It uses `DateTime.UtcNow` for `_openedAtUtc`/half-open transition checks
(`CheckHalfOpenTransitionUnderLock`). `System.TimeProvider` is not referenced anywhere in `apps/src` today.

Adopting `TimeProvider` here is practical (inject `TimeProvider` with a `TimeProvider.System` default,
replace the two `DateTime.UtcNow` call sites, use a hand-rolled `TimeProvider` subclass in tests since no
`Microsoft.Extensions.TimeProvider.Testing` package reference exists yet) but was not completed within this
session's scope. It remains a real, tracked gap, not a false "Ready" claim — see §11.

Separately noted: `apps/src/AI.Factory.Infrastructure/Resilience/CircuitBreaker.cs` and its
`CircuitBreakerAgentExecutorDecorator` are **not wired into either DI container** — they are exercised only
by `apps/tests/AI.Factory.Application.Tests/CircuitBreakerTests.cs` and are dead in production. Any future
`TimeProvider` migration should target `CircuitBreakerImpl`, not this unused class; whether to delete the
dead class is a separate cleanup decision, out of scope for this phase.

## 9. SQLite busy/locked coverage — unchanged, documented

**No test changes made in this pass.** `DatabaseResiliencePolicy`
(`apps/src/AI.Factory.Infrastructure/Persistence/DatabaseResiliencePolicy.cs`) classifies SQLite error code 5
(`SQLITE_BUSY`) and 6 (`SQLITE_LOCKED`) and retries with exponential backoff + jitter — this classifier-level
behavior is already covered by `DatabaseResiliencePolicyTests.cs`. There is no `PRAGMA busy_timeout` /
connection-string `Busy Timeout` configured; contention handling is entirely the application-level retry
path (EF's `CommandTimeout` is a different, unrelated setting for how long a single command may run).
`DatabaseConcurrencyStressTests.cs` and `ConcurrentApiLoadTests.cs` already exercise concurrent access
against a real SQLite file, which is the integration layer that can realistically be tested without a
provider-internal fault injector. Deterministically forcing a real `SQLITE_BUSY` through the full stack
remains impractical without either a fault-injecting test double for `Microsoft.Data.Sqlite` or an explicit
low-timeout test configuration that risks flakiness — carried forward as a documented limitation, not solved
in this pass.

## 10. Backup, restore, secret rotation, migration rollback

- **Backup / restore / DR (RPO 1h, RTO 15m)**: already present and adequate in
  `phase-17.4.10-disaster-recovery-runbook.md` §3–4 — hot SQLite `.backup` procedure (never a blind file
  copy), volume snapshot retention tiers, and a 7-step sequential restore procedure. Not modified this
  phase beyond the route-name fix (§3).
- **Secret rotation** (new, §4.5 of the production runbook): rolling-restart procedure for
  `OPENROUTER_API_KEY` and `OPERATIONAL_ACCESS_KEY`, with explicit verify-before-revoke ordering. Non-SQLite
  database credential rotation is called out as unimplemented/untested against this codebase rather than
  glossed over.
- **Migration rollback policy** (new, §4.6): EF migrations are forward-only; no downgrade command is
  supported against production; a failed migration is investigated and fixed forward or recovered via the
  DR restore procedure, never via a schema downgrade.

## 11. Final production readiness matrix

| Category | Status | Notes |
|---|---|---|
| API Reliability | Ready | Global exception handling, ProblemDetails, correlation IDs, retry/timeout, circuit breaker, bulkhead, and now correct 409 conflict mapping all in place and tested. |
| Worker Reliability | Ready | Degraded/Recovered/Faulted transitions now driven by real poll outcomes with a configurable policy; graceful shutdown, lease recovery, and no-double-execution were already covered and remain green. |
| Database Reliability | Ready with documented limitation | Retry, optimistic concurrency, unique-constraint→409, transactions, idempotency all covered; real `SQLITE_BUSY` reproduction through the full stack remains a documented test-layer limitation (§9), not a production behavior gap. |
| Security | Ready | Unchanged this phase — headers, rate limits, size limits, input validation, operational access, secret redaction, config validation were already in place. |
| Configuration & Secrets | Ready | `WorkerHealthOptions` added following the existing fail-fast `Validate()` pattern; no secrets in appsettings; sanitized diagnostics unchanged. |
| Containers & Deployment | Ready | Finding 4's HEALTHCHECK fix removes the one real restart-loop risk found; non-root, multi-stage, persistent volumes, migration ordering unchanged and correct. |
| Observability | Ready | New `WorkerDegraded`/`WorkerRecovered`/`WorkerFaulted` audit events and `WorkerPollFailure`/`WorkerDegraded`/`WorkerRecovered` metrics close the gap Finding 1 identified. |
| Operational Readiness | Ready with documented limitation | Start/stop/restart/failure-diagnosis/backup/restore/DR/migration-failure all documented and exercised; circuit breaker time-based testability (§8) is deferred, not falsely claimed done. |

## 12. Files created

- `apps/src/AI.Factory.Application/Configuration/WorkerHealthOptions.cs`
- `apps/tests/AI.Factory.Api.Tests/OperationalEndpointContractTests.cs`
- `.ai/context/phase-17.4.11-production-findings-remediation.md` (this file)

## 13. Files modified

- `apps/src/AI.Factory.Domain/Enums/AuditEventType.cs`
- `apps/src/AI.Factory.Application/Abstractions/IWorkerHealthCoordinator.cs`
- `apps/src/AI.Factory.Application/Abstractions/IOperationalMetrics.cs`
- `apps/src/AI.Factory.Infrastructure/Diagnostics/WorkerHealthCoordinator.cs`
- `apps/src/AI.Factory.Worker/OrchestrationRunWorker.cs`
- `apps/src/AI.Factory.Worker/Program.cs`
- `apps/src/AI.Factory.Api/Middleware/ApiExceptionMapper.cs`
- `Dockerfile.worker`
- `apps/tests/AI.Factory.Worker.Tests/WorkerHealthCoordinatorTests.cs`
- `apps/tests/AI.Factory.Worker.Tests/WorkerRestartRecoveryTests.cs`
- `apps/tests/AI.Factory.Api.Tests/ApiExceptionMapperTests.cs`
- `apps/tests/AI.Factory.Application.Tests/BulkheadIsolationTests.cs` (unrelated flake fix carried over from
  the in-progress session state — a fixed 20ms `Task.Delay` replaced with a poll loop)
- `.ai/context/phase-17.4.10-production-runbook.md`
- `.ai/context/phase-17.4.10-disaster-recovery-runbook.md`
- `.ai/context/phase-17.4.10-production-readiness-review.md`

## 14. Validation

```
dotnet build apps/AI.Factory.slnx        → 0 errors, 0 warnings (1 pre-existing unrelated CS8601 warning)
dotnet test apps/AI.Factory.slnx --no-build --blame-hang-timeout 90s
  → AI.Factory.Worker.Tests:      60/60 passed
  → AI.Factory.Application.Tests: 369/369 passed
  → AI.Factory.Api.Tests:         186/186 passed
  → Total: 615/615 passed (up from the 597 baseline stated in the phase brief)
```

```
docker compose config          → valid
docker compose build worker    → built (image ai-factory-worker:latest)
docker compose build api       → built (image ai-factory-api:latest)
docker compose up -d            → migrator ran and exited 0, api + worker started
docker compose ps (after ~20s) → api: Up (healthy), worker: Up (healthy)
docker compose exec worker ls /tmp/ai-factory-health → worker.live, worker.ready present (worker.degraded absent, as expected in Ready)
curl -sf http://localhost:8080/health/live → 200
docker compose down             → clean teardown
```

Live smoke test confirms the Finding 4 Dockerfile change: the worker container reports `healthy` via the
new `worker.live`-based HEALTHCHECK while in the normal Ready state, with the expected health files present.

Stress/Load/Resilience category filters were not run separately in this pass (the full unfiltered `dotnet
test` run already includes them and passed).

## 15. Remaining limitations

- Circuit breaker time abstraction (Finding 5) — not implemented; `CircuitBreakerImpl` still uses
  `DateTime.UtcNow`. Tests still use short real delays (`CircuitBreakerTests.cs`, `CircuitBreakerRegistryTests.cs`).
- SQLite busy/locked deterministic reproduction (Finding 6) — not implemented; classifier-level coverage
  only, as already documented in Phase 17.4.9.
- ~~Docker Compose / container build / live smoke validation — not executed in this session~~ — corrected in
  Phase 17.4.12: this was inconsistent with §14 above, which already documents a successful
  `docker compose config/build/up` run and live smoke test against the Finding 4 HEALTHCHECK change.
- Dead code noted but not removed: `AI.Factory.Infrastructure.Resilience.CircuitBreaker` and
  `CircuitBreakerAgentExecutorDecorator` are unused in both DI containers.
- Non-SQLite database credential rotation is documented as unimplemented/untested, not a supported procedure yet.
