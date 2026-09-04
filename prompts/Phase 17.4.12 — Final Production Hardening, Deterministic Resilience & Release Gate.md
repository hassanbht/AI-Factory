# Phase 17.4.12 — Final Production Hardening, Deterministic Resilience & Release Gate

## Mission

Implement the final production hardening and release-gate phase for the AI Factory platform.

This phase follows:

- Phase 17.4.1 — Global Exception Handling
- Phase 17.4.2 — Retry, Timeout & Resilience
- Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience
- Phase 17.4.4 — Graceful Shutdown, Cancellation Propagation & Background Worker Reliability
- Phase 17.4.5 — Security Hardening
- Phase 17.4.6 — Configuration & Secret Management
- Phase 17.4.7 — Database Resilience & Concurrency
- Phase 17.4.8 — Deployment & Container Production Readiness
- Phase 17.4.9 — Load, Stress & Failure Testing
- Phase 17.4.10 — Production Operational Runbook
- Phase 17.4.11 — Production Findings Remediation

The objective of this phase is to close the remaining production-readiness gaps identified in Phase 17.4.9 and Phase 17.4.11 and establish a deterministic, testable release gate.

The two explicitly deferred technical findings must now be addressed:

1. Circuit breaker tests currently rely partly on real time delays.
2. SQLite busy/locked failure paths are not fully deterministic in automated tests.

Additionally, the platform must gain a real, documented, testable backup/restore capability appropriate for its current SQLite deployment model.

---

# IMPORTANT WORKING RULES

Before modifying code:

1. Inspect the current repository state.
2. Read `AGENTS.md` and all applicable repository instructions.
3. Inspect existing implementations before creating replacements.
4. Reuse existing abstractions wherever possible.
5. Do not introduce duplicate resilience, clock, database, diagnostics, or configuration abstractions.
6. Preserve Clean Architecture boundaries.
7. Do not weaken existing tests merely to make the suite pass.
8. Do not replace a real production behavior with a fake implementation.
9. All diagnostics must remain secret-safe.
10. Existing behavior must remain backward compatible unless an intentional production bug is being fixed.

Run a full build and test suite before completion.

---

# 1. Deterministic Time for Circuit Breakers

## Problem

The current circuit breaker implementation and tests use real elapsed time and short delays for state transitions.

This creates:

- slower tests
- potential timing flakiness
- dependence on scheduler timing
- difficulty testing long break durations
- difficulty deterministically testing HalfOpen behavior

The circuit breaker must support deterministic time.

---

## Required Architecture

Use the modern .NET `TimeProvider` abstraction.

Do not introduce a custom clock abstraction unless the repository already contains one that must be reused.

The circuit breaker implementation must receive or resolve a `TimeProvider`.

Preferred dependency:

```csharp
TimeProvider
```

Production must use:

```csharp
TimeProvider.System
```

Tests should use a controllable/fake time provider.

If the appropriate Microsoft testing package is required for `FakeTimeProvider`, add it only where necessary.

---

## Circuit Breaker Changes

Refactor the circuit breaker implementation so all time-dependent logic uses `TimeProvider`.

This includes:

- circuit open timestamp
- break duration calculation
- transition from Open to HalfOpen
- probe timestamps
- diagnostics timestamps
- any retry or resilience timestamps owned by the circuit breaker

Do not use:

```csharp
DateTime.UtcNow
DateTimeOffset.UtcNow
Stopwatch
Task.Delay
```

inside circuit-breaker state transition logic where deterministic `TimeProvider` support is appropriate.

Existing public behavior must remain compatible.

---

## Required Tests

Add deterministic tests covering:

### Closed -> Open

- consecutive failures reach threshold
- circuit opens exactly at the expected time
- snapshot reflects correct timestamps

### Open behavior

- execution rejected before break duration expires
- no real delay required
- bulkhead must not be entered

### Open -> HalfOpen

Advance fake time.

Verify:

- transition occurs exactly when configured break duration expires
- no race depends on wall-clock timing

### HalfOpen trial limits

Verify:

- exactly configured number of probe requests are admitted
- additional probes are rejected
- concurrent access remains safe

### HalfOpen -> Closed

Verify:

- successful configured recovery behavior closes the circuit correctly

### HalfOpen -> Open

Verify:

- failed probe immediately reopens the circuit
- break duration restarts deterministically

### Long durations

Test a break duration such as:

- 5 minutes
- 30 minutes
- 1 hour

without real waiting.

---

# 2. Deterministic SQLite Busy / Locked Testing

## Problem

The previous phase documented that real SQLite lock contention did not consistently surface `SQLITE_BUSY` or `SQLITE_LOCKED` because the SQLite provider's busy timeout absorbed contention.

This phase must establish deterministic testing of database failure classification and resilience behavior.

Do not rely only on timing-based multi-thread contention.

---

## Required Design

Separate two responsibilities:

### A. Real database integration behavior

Tests may continue validating:

- concurrent writers
- optimistic concurrency
- unique constraint conflicts
- transaction rollback
- retry behavior

### B. Deterministic provider failure behavior

Create a controlled test mechanism capable of deterministically producing provider/database failures representing:

- SQLite busy
- SQLite locked
- SQLite IO errors where supported by current resilience classification

The test mechanism must exercise the real application resilience policy and failure classifier.

Do not merely unit test an enum or string comparison.

The tests must verify that the actual exception flow reaches:

```text
Provider exception
    ->
Database failure classification
    ->
Retry decision
    ->
Backoff policy
    ->
Final success or failure
    ->
Metrics / audit behavior
```

---

## Preferred Implementation Options

Prefer one of the following approaches depending on repository architecture:

### Option A — Test database command interceptor

Use EF Core interception or another existing database abstraction to deterministically inject database exceptions.

### Option B — Test provider adapter

Introduce a narrowly scoped test double at the infrastructure boundary that produces provider exceptions while preserving the production classification path.

### Option C — Existing abstraction

Reuse an existing database execution abstraction if the repository already has one.

Do not modify production database behavior solely to make tests easier.

---

## Required Scenarios

### Busy then success

Simulate:

```text
SQLITE_BUSY
SQLITE_BUSY
Success
```

Verify:

- retries occur
- operation eventually succeeds
- retry metrics increment
- final failure metrics do not increment
- retry delays follow configured policy
- no secret or SQL leakage occurs

### Locked until retry exhaustion

Simulate repeated:

```text
SQLITE_LOCKED
```

until retry budget is exhausted.

Verify:

- exact retry count
- final failure classification
- correct exception/result
- correct metrics
- correct audit behavior

### Non-transient database failure

Simulate a non-transient database failure.

Verify:

- no inappropriate retry
- failure is surfaced immediately

### Cancellation

Cancel during database retry/backoff.

Verify:

- cancellation stops further retries
- no resource leaks
- no transaction remains open

### Metrics

Verify deterministic metric increments for:

- retries
- transient failures
- permanent failures
- exhausted retries

---

# 3. Backup & Restore Capability

## Goal

The production runbook currently discusses disaster recovery, but the application must now have a concrete, safe, testable backup strategy appropriate for SQLite.

This phase must implement the minimum production-grade backup and restore foundation.

---

## Backup Requirements

Create a strongly typed configuration section.

Suggested name:

```text
BackupOptions
```

The exact naming may follow existing project conventions.

Configuration should support at minimum:

- Enabled
- BackupDirectory
- RetentionCount
- ValidateAfterBackup
- BackupTimeout
- OptionalCompression if appropriate

Validate configuration at startup.

Invalid production configuration must fail safely.

---

## Backup Service

Introduce a clean abstraction, for example:

```csharp
IDatabaseBackupService
```

The implementation must:

1. Identify the active SQLite database safely.
2. Create a consistent backup.
3. Avoid copying a partially written database unsafely.
4. Use SQLite-safe backup mechanisms where possible.
5. Write to a temporary file first.
6. Validate the completed backup.
7. Atomically move it into final location.
8. Apply retention cleanup.
9. Never log connection strings or secrets.

The backup naming should include:

- application/environment-safe prefix
- UTC timestamp
- uniqueness component if required

---

## Backup Metadata

Create a safe snapshot/result model containing:

- backup identifier
- created timestamp
- database provider
- backup size
- validation result
- retention cleanup result

Do not expose:

- connection strings
- absolute sensitive host paths if avoidable
- credentials
- secrets

---

# 4. Restore Safety

## Important

Do not implement an unsafe public HTTP endpoint that can directly restore a production database.

Restore must require an explicit operational mechanism.

Preferred options:

- dedicated CLI mode
- dedicated restore command
- explicit container/job command

Suggested pattern:

```text
--restore-database <backup-file>
```

or equivalent existing command-line conventions.

---

## Restore Requirements

The restore mechanism must:

1. Require explicit operator intent.
2. Validate the backup before replacing the active database.
3. Refuse to restore while the database is actively used unless explicitly running in a safe offline mode.
4. Create a pre-restore safety backup where practical.
5. Use temporary files and atomic replacement where supported.
6. Fail without destroying the current database if validation fails.
7. Return meaningful exit codes.
8. Produce sanitized logs.

Do not silently overwrite production data.

---

# 5. Backup/Restore Testing

Add automated tests covering:

### Backup creation

- backup file is created
- metadata is returned
- source database remains unchanged

### Data validity

- restored backup contains expected data

### Retention

Given more backups than `RetentionCount`:

- oldest backups are removed
- newest backups remain

### Failure safety

Simulate:

- inaccessible backup directory
- validation failure
- write failure
- cancellation

Verify:

- original database remains intact
- partial temporary files are cleaned up where possible

### Restore

Test:

```text
Database A
    ->
Create backup
    ->
Modify database
    ->
Restore backup
    ->
Database returns to expected previous state
```

This should be performed against isolated temporary test databases.

Never execute restore tests against production paths.

---

# 6. Release Readiness Gate

Create a formal release readiness abstraction or document-driven verification mechanism.

Do not build an unnecessary deployment platform.

The goal is to make the production acceptance criteria explicit and testable where practical.

Create:

```text
.ai/context/phase-17.4.12-final-production-hardening-release-gate.md
```

The document must include a release checklist.

---

## Required Release Gates

### Build

```powershell
dotnet build apps/AI.Factory.slnx
```

Must pass with:

- 0 errors
- 0 warnings unless explicitly documented as accepted

---

### Automated Tests

```powershell
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

Must pass completely.

---

### Resilience Tests

Run relevant categories, if traits already exist:

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"
dotnet test apps/AI.Factory.slnx --filter "Category=Load"
```

Document actual results.

---

### Docker Configuration

```powershell
docker compose config
```

Must pass.

---

### Docker Build

```powershell
docker compose build
```

Must pass.

---

### Migration

Validate the migrator behavior:

```text
migrator succeeds
    ->
api starts
    ->
worker starts
```

Also verify that API/Worker do not start when migration fails.

---

### Health

Verify:

```text
API /health/live
API /health/ready
Worker liveness
Worker readiness
Docker health
```

Worker Docker health must remain based on liveness, not readiness.

A transient dependency outage may degrade the worker but must not create a container restart loop.

---

### Restart Recovery

Verify:

- API restart
- Worker restart
- full stack restart
- persistent database survives restart
- worker recovery does not double-execute stale work

---

### Backup Validation

The release gate must verify at least:

```text
create backup
validate backup
restore isolated test copy
verify expected data
```

---

### Security

Verify:

- production diagnostics remain protected
- secrets do not appear in diagnostics
- secrets do not appear in logs
- production OpenAPI/Swagger behavior matches current security policy
- rate limiting still functions
- oversized requests still reject safely

---

# 7. Regression Review of Previous Findings

Explicitly verify that Phase 17.4.11 fixes remain correct.

## Worker degradation

Verify:

```text
Starting
    ->
Ready
    ->
Degraded
    ->
Ready
```

and:

```text
Ready
    ->
Faulted
```

where applicable.

Verify:

- lifecycle transitions
- metrics
- audit events
- health probe files

---

## Unique Constraint Conflict

Verify concurrent duplicate creation still results in:

```text
HTTP 409 Conflict
```

and does not regress to HTTP 500.

---

## Operational Endpoint Contract

Verify actual route contracts match:

- resilience
- database
- configuration
- deployment
- metrics

Documentation must not contain stale diagnostic paths.

---

# 8. Observability

Add metrics and audit events only where they provide operational value.

Potential backup metrics:

- BackupSucceeded
- BackupFailed
- BackupValidated
- RestoreSucceeded
- RestoreFailed

Follow the existing `IOperationalMetrics` design.

Audit events should be added only for meaningful operational lifecycle events.

All messages must pass existing sensitive-data sanitization.

---

# 9. API Diagnostics

If backup diagnostics are exposed through the API, they must:

- be read-only
- expose no secret paths or credentials
- be protected by `OperationalEndpointSecurityMiddleware`
- be rate limited
- never provide a public restore trigger

A backup status endpoint is optional.

A public restore endpoint is forbidden.

---

# 10. Clean Architecture Constraints

Maintain these boundaries:

```text
Domain
    ->
Application
    ->
Infrastructure
    ->
API / Worker
```

Rules:

- Application defines abstractions and contracts.
- Infrastructure implements SQLite backup/restore behavior.
- API exposes safe diagnostics only.
- Worker/API composition roots bind options.
- Domain should only receive concepts that are truly domain-level.
- Do not place SQLite implementation details in Application abstractions beyond what is necessary.

---

# 11. Required Test Suites

Add or extend tests for:

## Circuit breaker

- deterministic fake time
- open duration
- HalfOpen transition
- concurrent probes
- recovery
- reopen

## Database resilience

- deterministic busy
- deterministic locked
- retry success
- retry exhaustion
- cancellation
- metrics

## Backup

- successful backup
- validation
- retention
- failure safety
- restore correctness

## Docker / production

- worker liveness semantics
- restart behavior
- migration ordering

## Regression

- worker degradation
- 409 unique conflict mapping
- operational endpoint contracts

---

# 12. Documentation

Create:

```text
.ai/context/phase-17.4.12-final-production-hardening-release-gate.md
```

Document:

1. Phase objective
2. Remaining findings from 17.4.11
3. TimeProvider architecture
4. Deterministic circuit breaker testing
5. Deterministic SQLite failure testing
6. Backup architecture
7. Backup procedure
8. Restore safety model
9. Retention policy
10. Release readiness checklist
11. Regression verification
12. Docker validation
13. Known limitations
14. Explicit production go/no-go criteria

Also update the production runbook where necessary.

---

# 13. Definition of Done

Phase 17.4.12 is complete only when:

1. Circuit breaker time-dependent behavior is testable without real waiting.
2. Circuit breaker tests use controllable time.
3. SQLite busy/locked resilience paths have deterministic automated coverage.
4. Retry exhaustion and cancellation paths are deterministic.
5. A safe SQLite backup mechanism exists.
6. Backup validation exists.
7. Retention cleanup exists.
8. Restore is explicit and cannot accidentally overwrite production data.
9. Restore tests verify real data recovery on isolated databases.
10. No public HTTP restore endpoint exists.
11. Existing Phase 17.4.11 fixes remain intact.
12. Docker worker health remains liveness-based.
13. Build succeeds.
14. Full automated suite succeeds.
15. Resilience/load/stress suites succeed.
16. Docker Compose configuration succeeds.
17. Docker production images build successfully.
18. Required smoke tests succeed.
19. All documentation is updated.
20. No secrets, connection strings, passwords, API keys, or sensitive database details are exposed.

---

# Required Final Output

After implementation, provide a detailed walkthrough containing:

## Summary

- What was implemented.

## Circuit Breaker

- How `TimeProvider` was integrated.
- Which real-time dependencies were removed.
- Which deterministic tests were added.

## SQLite Resilience

- How busy/locked failures are deterministically injected.
- Which real production path is exercised.
- Retry and cancellation behavior.

## Backup & Restore

- Architecture.
- Configuration.
- Backup consistency strategy.
- Validation.
- Retention.
- Restore safety.
- CLI/operational usage.

## Release Gate

- Build result.
- Full test result.
- Resilience category result.
- Load category result.
- Stress category result.
- Docker config result.
- Docker build result.
- Smoke test result.
- Backup/restore verification.

## Test Counts

Provide exact counts:

```text
AI.Factory.Application.Tests: X passed
AI.Factory.Api.Tests: X passed
AI.Factory.Worker.Tests: X passed
Total: X passed, 0 failed
```

## Problems Found

List all real findings honestly.

Do not silently patch a finding and omit it from the final report.

## Deferred Limitations

List anything that remains intentionally deferred.

---

# Final Instruction

Do not stop after implementing only the deferred F5 and F6 findings.

This phase is the final production hardening gate.

The implementation must finish with a clear production readiness decision:

```text
GO
or
NO-GO
```

The decision must be justified by actual automated validation and smoke-test evidence, not assumptions.