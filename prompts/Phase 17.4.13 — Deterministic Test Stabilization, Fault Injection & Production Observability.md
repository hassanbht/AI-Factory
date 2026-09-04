# Phase 17.4.13 — Deterministic Test Stabilization, Fault Injection & Production Observability

## Context

Phase 17.4.12 completed the Final Production Hardening and Release Gate with an explicit **GO** decision.

The platform currently has:

- Clean build with 0 warnings and 0 errors.
- 625/625 tests passing in the clean release-gate run.
- Deterministic circuit-breaker tests using `TimeProvider`.
- Deterministic SQLite BUSY/LOCKED resilience policy tests.
- Production Docker deployment.
- Migrator → API/Worker startup ordering.
- Worker lifecycle health coordination.
- Liveness-based Docker worker health checks.
- SQLite backup, validation, retention and CLI-only restore.
- Operational diagnostics and metrics.
- Production operational runbook.

However, Phase 17.4.12 documented several remaining gaps that should now be addressed as a focused stabilization and observability phase.

This phase must NOT introduce unnecessary product features.

The primary goals are:

1. Eliminate known flaky automated tests where deterministic seams are technically possible.
2. Add empirical migration-failure startup-block verification.
3. Improve production observability around dependency degradation and recovery.
4. Add automated regression checks for Docker deployment behavior.
5. Produce an evidence-based stability report.

---

# GLOBAL EXECUTION RULES

Before modifying code:

1. Read:
   - `AGENTS.md`
   - `.ai/context/phase-17.4.10-production-runbook.md`
   - `.ai/context/phase-17.4.11-production-findings-remediation.md`
   - `.ai/context/phase-17.4.12-final-production-hardening-release-gate.md`

2. Inspect the current repository state.

3. Do NOT assume that the implementation described in previous phase documentation exactly matches the current source.

4. Verify actual source code before modifying anything.

5. Preserve:
   - Clean Architecture boundaries.
   - Existing public contracts unless change is justified.
   - Existing API compatibility.
   - Secret sanitization.
   - Existing health endpoint semantics.
   - SQLite production safety.
   - Existing Docker deployment architecture.

6. Do not silently change behavior merely to make tests pass.

If an existing production defect is discovered, document:

- Root cause.
- Impact.
- Why existing tests did not catch it.
- Fix.
- Regression coverage.

---

# WORKSTREAM A — ELIMINATE KNOWN TEST FLAKINESS

Phase 17.4.12 documented intermittent failures in real-concurrency / real-timing tests.

Investigate each one individually.

The goal is NOT to force all concurrency tests into artificial determinism.

The goal is to remove unnecessary timing assumptions and shared-resource races.

---

## A1 — Rate Limit Burst Test Stabilization

Investigate:

`RateLimitBurstStressTests.Burst_AboveLimit_...`

The phase documentation mentions a possible off-by-one race in a real token-bucket burst test.

### Requirements

1. Inspect the actual rate limiter implementation.

2. Determine whether the flakiness is caused by:

   - Test scheduling.
   - Token replenishment timing.
   - Shared state.
   - Parallel execution.
   - Incorrect test assumptions.
   - A real production off-by-one defect.

3. Do NOT simply loosen assertions without understanding the behavior.

4. If the implementation is correct and the test assumes an impossible exact ordering:
   - Replace timing-sensitive assertions with invariant-based assertions.

Examples of useful invariants:

- Requests never exceed the allowed effective capacity.
- Rejected requests contain correct status.
- Successful requests do not exceed configured permit semantics.
- `Retry-After` is present where required.
- Rate-limit recovery occurs after the configured window.

5. If practical, inject a controllable clock.

6. Do not replace realistic integration coverage with pure mocks.

### Required regression coverage

Run the affected test at least:

- 20 consecutive times in isolation.
- 10 consecutive times under the relevant category filter.

Report the results.

---

## A2 — Concurrent Duplicate Slug Test

Investigate:

`DatabaseConcurrencyStressTests.ConcurrentDuplicateSlugCreates_...`

Phase 17.4.12 suggests that an existing test factory may share a single SQLite connection under concurrent requests.

### Requirements

1. Inspect the actual test infrastructure.

2. Determine whether multiple concurrent requests incorrectly share:

   - A DbConnection.
   - A DbContext.
   - A transaction.
   - Test server state that is not concurrency-safe.

3. The production behavior must remain realistic:

- Multiple requests.
- Multiple DbContexts.
- Real SQLite persistence.
- Unique constraint enforcement.

4. Fix the test infrastructure if it creates an artificial race.

5. Do NOT weaken the actual production concurrency scenario.

6. Preserve existing expected semantics:

- Exactly one creation succeeds.
- Duplicate rows are impossible.
- Losing requests map to the intended conflict behavior.
- Database state remains consistent.

### Required regression coverage

Run:

- The test at least 20 consecutive times.
- A larger concurrency batch if practical.
- Relevant API stress tests afterward.

Document success/failure counts.

---

## A3 — Cancellation During Retry Backoff

Investigate:

`RetryTimeoutStressTests.Cancellation_DuringBackoffDelay_StopsBeforeSecondAttempt`

Phase 17.4.12 documented this as a real-time cancellation race.

### Requirements

1. Inspect the retry implementation.

2. Determine whether a controllable abstraction already exists.

3. Prefer:

- `TimeProvider`
- Injectable delay abstraction
- Deterministic synchronization primitive

over fixed:

- `Task.Delay(20)`
- Arbitrary sleeps
- Race-dependent cancellation timing.

4. The test must prove the real invariant:

> Cancellation during retry backoff prevents the next attempt from starting.

5. Do not merely increase delay values.

6. Avoid making the production implementation test-specific.

### Required regression coverage

Run:

- The affected test 50 consecutive times.
- The resilience category suite.
- The stress category suite.

Report results.

---

# WORKSTREAM B — EMPIRICAL MIGRATION FAILURE FAULT INJECTION

Phase 17.4.12 verified migration startup blocking structurally but did not empirically fault-inject a migration failure.

This phase must close that gap.

---

## B1 — Automated Failure Injection Strategy

Implement a safe automated way to validate:

> API and Worker must not start when Migrator fails.

Do NOT corrupt real migrations.

Do NOT modify production migrations solely for testing.

Acceptable strategies include:

- A test-only migration failure switch.
- An environment-variable-controlled migration failure injection only available in non-production test scenarios.
- A dedicated test compose override.
- A controlled invalid database target.
- Another safe deterministic approach.

The mechanism must never accidentally create a production backdoor.

---

## B2 — Required Assertions

The automated scenario must verify:

1. Migrator exits with a non-zero status.

2. API does not become ready.

3. Worker does not become ready.

4. API/Worker do not start as healthy application services.

5. Logs clearly identify migration failure.

6. No partial service startup occurs.

7. The test environment cleans up afterward.

---

## B3 — Docker Compose Regression Coverage

Add an automated deployment-level verification where practical.

If xUnit-based Docker integration tests are too heavy for the normal suite:

- Place them in a clearly categorized suite.
- For example:

`[Trait("Category", "ContainerIntegration")]`

They must be runnable explicitly.

Document the required command.

Do NOT make the standard developer test loop excessively slow unless necessary.

---

# WORKSTREAM C — PRODUCTION OBSERVABILITY IMPROVEMENTS

The system already has operational metrics and diagnostics.

This phase should improve signal quality, not expose sensitive data.

---

## C1 — Worker Lifecycle Visibility

Verify that the following transitions are observable:

- Starting
- Ready
- Degraded
- Recovered
- Stopping
- Faulted

Inspect the current:

- `IWorkerHealthCoordinator`
- Metrics
- Audit events
- Logs

Identify gaps.

### Requirement

Every meaningful transition should have at least one observable signal.

Possible signals:

- Metric counter.
- Structured log.
- Audit event.

Do not generate excessive audit noise for repeated identical states.

Transitions are meaningful.

Repeated polling failures that do not change lifecycle state should generally not generate unlimited audit records.

---

## C2 — Dependency Degradation Observability

When external dependencies repeatedly fail:

- Worker degradation must be visible.
- Recovery must be visible.
- Circuit breaker transitions must be visible.
- Operational metrics must reflect the event.

Inspect whether the existing system can distinguish:

- Healthy.
- Transient failure.
- Sustained degradation.
- Circuit open.
- Recovery.

Do not expose provider credentials or connection strings.

---

## C3 — Operational Metrics Contract Tests

Create or extend contract tests for important operational metrics.

Verify that counters remain coherent after representative lifecycle transitions.

Examples:

- WorkerDegraded increments once per degradation transition.
- WorkerRecovered increments once per recovery transition.
- CircuitOpened increments on actual transition.
- BackupSucceeded increments only after successful completion.
- BackupFailed increments on failure.
- RestoreSucceeded/Failed behave correctly.

Avoid tests coupled to internal implementation details.

---

# WORKSTREAM D — BACKUP/RESTORE REGRESSION HARDENING

Phase 17.4.12 implemented backup and restore successfully.

This phase must perform targeted regression review.

---

## D1 — Backup Retention Concurrency

Inspect whether concurrent backup CLI invocations could create races around retention cleanup.

You do NOT need to support arbitrary simultaneous backup operations if the architecture intentionally treats backup as an administrative operation.

But behavior must be explicit.

Choose and document one strategy:

### Option A — Serialize backup operations

Use a safe process-level or filesystem lock.

### Option B — Explicitly reject concurrent backup

Return a clear failure indicating another backup operation is active.

### Option C — Demonstrate current implementation is safe

Only if proven.

Do not introduce distributed locking unless truly required.

---

## D2 — Restore Safety Regression

Verify again that:

1. Restore is CLI-only.
2. No HTTP restore endpoint exists.
3. Backup validation happens before active DB replacement.
4. Active services are not serving traffic during restore.
5. Failed validation never touches active database.
6. Temporary files and WAL/SHM sidecars are cleaned.
7. Secrets and connection strings never appear in results or logs.

Add tests only where coverage is missing.

---

# WORKSTREAM E — PRODUCTION CONFIGURATION VALIDATION

Review the current production configuration.

Specifically inspect:

- Docker Compose.
- Production appsettings.
- Backup volume.
- Data volume.
- Worker health check.
- API health check.
- Resource limits.
- Restart behavior.
- Migration ordering.
- Required environment variables.

---

## E1 — Configuration Regression Tests

Where practical, add automated tests that inspect deployment configuration files.

Important invariants include:

1. Worker container health uses liveness, not readiness.

2. API health check targets a valid endpoint.

3. API and Worker depend on successful migrator completion.

4. Persistent data volume exists.

5. Backup volume exists and is separate from primary data.

6. Containers run as non-root where expected.

7. Production secrets are not committed into configuration.

8. Swagger remains disabled if production policy requires it.

Avoid brittle full-file snapshot tests.

Test meaningful deployment invariants.

---

# WORKSTREAM F — REPEATED STABILITY GATE

After implementation, perform a stability-oriented validation.

---

## F1 — Build

Run:

```powershell
dotnet build apps/AI.Factory.slnx
```

Required:

- 0 errors.
- 0 warnings.

---

## F2 — Full Suite Repetition

Run the full suite at least:

```powershell
dotnet test apps/AI.Factory.slnx --no-build --blame-hang-timeout 90s
```

Repeat it at least 5 times.

Report every run separately.

Example:

| Run | Passed | Failed | Duration |
|---|---:|---:|---:|
| 1 | X | 0 | ... |
| 2 | X | 0 | ... |
| 3 | X | 0 | ... |
| 4 | X | 0 | ... |
| 5 | X | 0 | ... |

If any test fails:

- Stop.
- Investigate.
- Do not claim stability until root cause is understood.

Do not simply rerun until green and ignore the failure.

---

## F3 — Category Repetition

Run at least:

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Load"
```

Repeat the previously flaky categories where relevant.

Report results.

---

# WORKSTREAM G — DOCUMENTATION

Create:

`.ai/context/phase-17.4.13-deterministic-stability-observability.md`

The document must include:

## 1. Phase objective

## 2. Initial known instability findings

## 3. Rate limiter investigation

Include:

- Root cause.
- Whether production behavior was defective.
- Test stabilization approach.
- Regression evidence.

## 4. SQLite duplicate-slug concurrency investigation

Include:

- Test infrastructure findings.
- Connection/DbContext behavior.
- Production semantics.
- Fix.

## 5. Retry cancellation determinism

Include:

- Previous timing race.
- Deterministic mechanism.
- Regression results.

## 6. Migration failure fault injection

Include:

- Strategy.
- Safety boundary.
- Assertions.
- Actual observed results.

## 7. Worker lifecycle observability

Include lifecycle model and signals.

## 8. Dependency degradation visibility

## 9. Operational metrics contract coverage

## 10. Backup/restore concurrency review

## 11. Deployment configuration regression coverage

## 12. Docker validation

## 13. Repeated full-suite stability results

Include all 5 runs.

## 14. Remaining non-deterministic tests

If any remain, explicitly explain why.

## 15. Problems found

Do not silently hide them.

## 16. Deferred limitations

## 17. Updated production readiness decision

Use one of:

- GO
- GO WITH KNOWN LIMITATIONS
- NO-GO

The decision must be evidence-based.

---

# TESTING REQUIREMENTS

At minimum:

```powershell
dotnet build apps/AI.Factory.slnx
```

```powershell
dotnet test apps/AI.Factory.slnx --no-build --blame-hang-timeout 90s
```

Run the full suite 5 times.

Also run:

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Load"
```

If implemented:

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=ContainerIntegration"
```

Docker validation:

```powershell
docker compose config
docker compose build
docker compose up -d
docker compose ps
```

Verify:

- Migrator ordering.
- API healthy.
- Worker healthy.
- Worker health is liveness-based.
- Persistent data volume works.
- Backup volume remains separate.
- Migration failure test behaves correctly.

Clean up test resources afterward.

---

# DEFINITION OF DONE

Phase 17.4.13 is complete only when:

1. Each previously documented flaky test has been investigated.
2. Artificial timing assumptions have been removed where possible.
3. Production defects discovered during investigation are fixed.
4. Genuine non-determinism is documented rather than hidden.
5. Migration failure blocking has empirical automated coverage.
6. Worker lifecycle transitions have clear operational visibility.
7. Dependency degradation and recovery are observable.
8. Operational metrics have regression coverage.
9. Backup/restore concurrency behavior is explicitly safe.
10. Deployment configuration has meaningful invariant tests.
11. Docker deployment validation passes.
12. Build has 0 warnings and 0 errors.
13. Full suite passes in 5 consecutive runs, OR any remaining failures have a fully understood and explicitly documented root cause.
14. All problems found are documented.
15. Production readiness decision is evidence-based.
16. No secrets, credentials, or connection strings are exposed.
17. Existing Phase 17.4.12 backup/restore and release-gate behavior remains intact.

Do not commit unless explicitly requested.