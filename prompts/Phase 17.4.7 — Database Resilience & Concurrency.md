# Phase 17.4.7 — Database Resilience & Concurrency

## Mission

Implement Phase 17.4.7 for the AI Factory platform.

The goal of this phase is to make database access production-ready under:

- transient database failures,
- SQLite/PostgreSQL provider interruptions,
- concurrent updates,
- duplicate command execution,
- race conditions,
- EF Core `DbUpdateConcurrencyException`,
- database busy/locked conditions,
- safe transaction boundaries,
- retry-safe persistence operations.

This phase MUST build on the existing architecture from:

- Phase 17.3 — Observability, Structured Logging, Audit Trail & Operational Diagnostics
- Phase 17.4.1 — Global Exception Handling & Problem Details
- Phase 17.4.2 — Retry, Timeout & Resilience
- Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience
- Phase 17.4.4 — Graceful Shutdown, Cancellation Propagation & Background Worker Reliability
- Phase 17.4.5 — Security Hardening
- Phase 17.4.6 — Configuration & Secret Management

Do NOT introduce duplicate resilience abstractions if equivalent components already exist.

Follow Clean Architecture and the existing repository/service conventions.

---

# 0. Mandatory Pre-Work

Before making ANY code changes:

1. Read `AGENTS.md`.
2. Read all relevant files under `.ai/context/`.
3. Read the following Phase context files in particular:

   - `.ai/context/phase-17.3-observability.md`
   - `.ai/context/phase-17.4.1-global-exception-handling.md`
   - `.ai/context/phase-17.4.2-retry-timeout-resilience.md`
   - `.ai/context/phase-17.4.3-circuit-breaker-bulkhead.md`
   - `.ai/context/phase-17.4.4-graceful-shutdown.md`
   - `.ai/context/phase-17.4.5-security-hardening.md`
   - `.ai/context/phase-17.4.6-configuration-secret-management.md`

4. Inspect the existing implementation before creating anything new:

   - `FactoryDbContext`
   - all EF Core entity configurations
   - repositories
   - Unit of Work / transaction abstractions, if present
   - orchestration execution engine
   - workflow engine
   - task state management
   - project repository
   - work item repository
   - approval repository
   - orchestration run repository
   - agent run repository
   - audit log repository
   - existing resilience abstractions
   - `ResilienceExecutor`
   - `IFailureClassifier`
   - `ApiExceptionMapper`
   - existing health checks
   - existing operational metrics
   - API and Worker composition roots

5. Before adding a new abstraction, search the entire solution for an equivalent abstraction.

6. Preserve existing public behavior unless explicitly changed by this phase.

7. Do not replace working architecture with a generic framework architecture.

---

# 1. Primary Goals

Implement the following capabilities.

## 1.1 Database transient failure resilience

Database operations must tolerate temporary failures where safe.

Examples include:

- temporary connection failures,
- provider/network interruptions,
- database busy conditions,
- SQLite locked conditions,
- temporary resource exhaustion,
- transient provider failures.

Retries MUST NOT be blindly applied to every database operation.

Only operations that are safe to retry automatically should be retried.

---

## 1.2 Optimistic concurrency

Protect mutable business entities from silent lost updates.

Concurrent changes must not silently overwrite another writer's changes.

The system should detect concurrency conflicts and return a meaningful domain/application failure.

---

## 1.3 Transaction boundaries

Multi-step database state changes must have explicit and safe transaction boundaries.

Examples may include:

- workflow transitions,
- orchestration state changes,
- task state changes,
- approval decisions,
- multi-entity updates.

Do not wrap every read operation in a transaction.

Do not create unnecessarily large transactions.

---

## 1.4 Retry-safe persistence

Database retries must not accidentally cause:

- duplicate audit records,
- duplicate orchestration runs,
- duplicate approvals,
- duplicate state transitions,
- duplicate background work.

---

## 1.5 Database diagnostics

Expose operationally useful database resilience information without exposing:

- connection strings,
- credentials,
- provider secrets,
- SQL statements containing sensitive data.

---

# 2. Database Resilience Configuration

Create strongly typed options if an equivalent configuration type does not already exist.

Suggested location:

`AI.Factory.Application/Configuration/DatabaseResilienceOptions.cs`

Suggested structure:

```csharp
public sealed class DatabaseResilienceOptions
{
    public bool EnableRetries { get; init; } = true;

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } =
        TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxRetryDelay { get; init; } =
        TimeSpan.FromSeconds(2);

    public bool EnableOptimisticConcurrency { get; init; } = true;

    public TimeSpan CommandTimeout { get; init; } =
        TimeSpan.FromSeconds(30);

    public void Validate();
}
```

The exact shape may differ if existing project conventions require it.

Requirements:

- fail fast on invalid configuration,
- no secrets in diagnostics,
- API and Worker configuration parity,
- environment overrides must continue to work,
- do not duplicate `DatabaseOptions` from Phase 17.4.6.

---

# 3. Provider-Aware Database Resilience

The AI Factory platform may support different providers.

Do not hard-code resilience behavior that only works for one provider.

Create or extend an abstraction if needed.

Suggested abstraction:

```csharp
IDatabaseResiliencePolicy
```

or equivalent.

It should answer questions such as:

- is this exception transient?
- is this operation retryable?
- what retry strategy applies?
- how should provider-specific busy/locked conditions be handled?

Potential classifications:

```text
Transient
Busy
Locked
Timeout
Connection
Concurrency
Permanent
Unknown
```

Do not create duplicate failure classification logic.

Reuse `IFailureClassifier` where appropriate.

The database-specific layer may provide additional provider knowledge and feed into the existing failure classification architecture.

---

# 4. Database Execution Strategy

Inspect whether EF Core execution strategies already provide part of this functionality.

Prefer provider-native EF Core mechanisms when appropriate.

Do NOT create a second competing retry pipeline around EF Core if:

- an existing provider execution strategy already handles the failure safely,
- the existing `ResilienceExecutor` can be reused appropriately.

The final architecture must avoid nested uncontrolled retries such as:

```text
HTTP Retry
    ->
ResilienceExecutor Retry
    ->
Database Retry
    ->
EF Execution Strategy Retry
```

without clear limits.

Document the final retry ownership.

---

# 5. Retry Safety Rules

Automatic retries are allowed only when the operation is known to be safe.

The implementation must distinguish between:

## Safe retry candidates

Examples:

- read queries,
- idempotent updates,
- provider-level execution strategy retries,
- transactions replayed entirely by EF execution strategy.

## Dangerous retry candidates

Examples:

- generating a new entity with a new random identifier,
- writing an audit event without idempotency,
- creating an orchestration run,
- creating an approval request,
- enqueueing background work.

These MUST NOT produce duplicates when retried.

If an operation is retried as part of a transaction, the entire logical unit must be retry-safe.

---

# 6. Optimistic Concurrency

Identify mutable aggregate/entity types where lost updates are meaningful.

Likely candidates include existing entities such as:

- Project
- WorkItem
- ApprovalRequest
- OrchestrationRun
- AgentRun
- workflow/task state entities

Do not blindly add concurrency tokens to every table.

For selected entities, add an optimistic concurrency mechanism.

Preferred approaches:

- EF Core concurrency token,
- version field,
- row version where provider-compatible.

SQLite compatibility must be considered.

If `rowversion` is not portable, use a provider-neutral concurrency version approach.

For example:

```csharp
public long Version { get; private set; }
```

or an equivalent approach.

Requirements:

1. concurrent writes must be detected,
2. stale updates must fail deterministically,
3. conflicts must not silently overwrite newer data,
4. conflicts must be classified explicitly,
5. API responses must map appropriately.

---

# 7. Concurrency Exception Model

Do not expose raw EF Core exceptions directly to the application/API.

Create or reuse an application-level exception such as:

```text
ConcurrencyConflictException
```

if no equivalent already exists.

The exception should represent:

> The resource changed after the caller loaded its state.

Do not include:

- SQL,
- connection strings,
- internal EF metadata.

Update:

- `IFailureClassifier`
- `ApiExceptionMapper`

so concurrency conflicts produce an appropriate API contract.

Recommended mapping:

```text
HTTP 409 Conflict
error code: concurrency_conflict
title: Resource was modified
detail: The resource was changed by another operation. Please reload and retry.
```

Preserve the existing ProblemDetails format and correlation ID behavior.

---

# 8. Repository Concurrency Handling

Repositories must not silently swallow:

```text
DbUpdateConcurrencyException
```

Create a consistent handling strategy.

For example:

```text
DbUpdateConcurrencyException
    ->
ConcurrencyConflictException
    ->
FailureClassifier
    ->
ProblemDetails / Orchestrator handling
```

Do not copy-paste exception translation into every repository if a central persistence boundary can handle it.

Prefer the smallest existing integration point.

---

# 9. Transaction Boundary Review

Review multi-step write operations.

At minimum inspect:

- workflow transitions,
- task state transitions,
- orchestration execution,
- approval decisions,
- project/work item state updates.

For each important operation, determine whether:

```text
operation
    ->
multiple entity changes
    ->
SaveChanges
```

requires atomic behavior.

Introduce explicit transaction boundaries only where necessary.

Requirements:

- short-lived transactions,
- cancellation support,
- correct rollback behavior,
- no long-running external agent execution inside a database transaction,
- no network call while holding a database transaction open.

This is critical.

Correct pattern:

```text
Read / reserve state
    ->
Commit

External dependency execution
    ->
Commit result state
```

Avoid:

```text
BeginTransaction
    ->
Call LLM
    ->
Call agent
    ->
Build project
    ->
Run tests
    ->
Commit
```

---

# 10. Idempotency and Duplicate Protection

Review retry-sensitive write operations.

At minimum inspect:

- orchestration run creation,
- approval request creation,
- audit event creation,
- workflow transition recording,
- agent run creation.

Where a retry could create duplicates, implement the smallest compatible idempotency protection.

Possible approaches:

- deterministic operation IDs,
- unique constraints,
- existing correlation/operation IDs,
- existing execution IDs,
- existing run IDs.

Do not introduce a global idempotency framework unless the existing architecture genuinely requires one.

Prefer existing:

- `CorrelationId`,
- `OperationId`,
- `RunId`,
- execution IDs,

introduced in earlier phases.

---

# 11. SQLite Busy and Locked Resilience

The current environment may use SQLite.

Explicitly consider:

```text
SQLITE_BUSY
SQLITE_LOCKED
```

These should not immediately be treated as permanent failures when retrying is safe.

Implement bounded handling consistent with the existing resilience configuration.

Requirements:

- bounded retries,
- cancellation support,
- no infinite retry,
- exponential or existing project-standard backoff,
- diagnostics visibility.

Do not add a custom SQLite retry loop if EF/provider configuration already solves the issue safely.

Document the chosen strategy.

---

# 12. Database Command Timeout

Apply database command timeout through strongly typed configuration.

Requirements:

- bounded positive duration,
- validated at startup,
- API and Worker parity,
- no hard-coded production timeout,
- diagnostics may expose timeout configuration but not connection strings.

If provider configuration requires integer seconds, convert carefully.

---

# 13. Database Health Checks

Extend database health checks.

At minimum:

## Liveness

The process is running.

Do not make liveness fail merely because the database is temporarily unavailable.

## Readiness

Verify that the database dependency is usable.

Database readiness failure must:

- be sanitized,
- preserve correlation information where applicable,
- not expose connection strings.

Potential readiness states:

```text
Healthy
Degraded
Unhealthy
```

Use the project's existing health check conventions.

---

# 14. Operational Metrics

Extend the existing `IOperationalMetrics`.

Add database resilience metrics only if equivalent metrics do not already exist.

Suggested counters:

```text
DatabaseOperations
DatabaseFailures
DatabaseRetries
DatabaseTransientFailures
DatabaseConcurrencyConflicts
DatabaseBusyFailures
DatabaseLockedFailures
DatabaseTransactionFailures
```

Suggested timing:

```text
DatabaseOperationDuration
```

Requirements:

- thread-safe,
- no raw sensitive SQL,
- no fabricated timing values,
- follow existing metric conventions from Phase 17.3 and 17.4.3.

Do not build Prometheus integration in this phase.

The existing snapshot architecture should be extended instead.

---

# 15. Audit Events

Review existing `AuditEventType`.

Add only genuinely missing database resilience events.

Potential events:

```text
DatabaseRetry
DatabaseConcurrencyConflict
DatabaseTransactionFailed
DatabaseOperationFailed
```

Do not add redundant events if existing audit event types already cover the outcome.

Audit entries must use:

- correlation context,
- operation context,
- failure classification,
- sanitized summaries.

Never audit:

- connection strings,
- credentials,
- SQL with sensitive parameters.

---

# 16. Database Diagnostics

Extend diagnostics in the smallest compatible way.

Recommended endpoint:

```text
GET /api/operations/database
```

or integrate database information into an existing operational diagnostics endpoint if that is cleaner.

Diagnostics may expose:

- provider name,
- readiness status,
- configured command timeout,
- retry enabled,
- retry limits,
- recent database failure count,
- concurrency conflict count,
- transient failure count.

Diagnostics MUST NOT expose:

- connection strings,
- usernames,
- passwords,
- server secrets,
- raw SQL.

Protect the endpoint using the existing:

- operational endpoint security middleware,
- rate limiting,
- correlation middleware.

---

# 17. Failure Classification Integration

Update the existing `IFailureClassifier` conservatively.

At minimum distinguish:

```text
DbUpdateConcurrencyException
ConcurrencyConflictException
database timeout
database busy
database locked
transient database provider failure
permanent database failure
unknown database failure
```

Reuse existing categories where appropriate.

Do not create excessive new global `FailureCategory` values unless necessary.

Preferred approach:

- concurrency conflict -> existing conflict semantics if present,
- timeout -> `Timeout`,
- transient provider/database dependency -> `Infrastructure` or `Provider`,
- unknown -> `Unknown`.

Document the final mapping.

---

# 18. Resilience Pipeline Interaction

Explicitly document interaction between:

- API request timeout,
- operation timeout,
- `ResilienceExecutor`,
- circuit breaker,
- bulkhead,
- database retries,
- EF execution strategy.

The implementation MUST avoid retry multiplication.

Define ownership clearly.

Recommended principle:

```text
Database layer owns provider/database transient retries.
ResilienceExecutor owns external dependency retries.
Circuit breakers/bulkheads protect external dependencies.
API does not independently retry database writes.
```

Adjust if existing architecture requires a different design, but document it.

---

# 19. API Error Contract

Ensure database-related failures are mapped correctly.

Suggested mappings:

| Failure | HTTP |
|---|---:|
| Concurrency conflict | 409 |
| Database timeout | 504 or existing timeout policy |
| Temporary database dependency failure | 503 |
| Unknown database failure | 500 |

All responses must:

- use RFC ProblemDetails,
- preserve `X-Correlation-ID`,
- avoid sensitive data leakage,
- use sanitized details.

Do not expose raw provider exceptions.

---

# 20. Background Worker Reliability

Review Worker database behavior.

Ensure:

- cancellation tokens propagate,
- transient database failures do not cause uncontrolled retry storms,
- concurrency conflicts are handled deterministically,
- shutdown does not leave an open transaction intentionally active,
- worker retry behavior does not duplicate work.

Reuse the Phase 17.4.4 graceful shutdown infrastructure.

Do not add a second worker execution loop.

---

# 21. EF Core Migration Requirements

If concurrency/version columns or unique constraints require schema changes:

1. Create an EF Core migration.
2. Use the project's existing migration conventions.
3. Verify migration generation.
4. Apply the migration to the development database if the project workflow requires it.
5. Verify existing data compatibility.

Do not add unnecessary migrations.

---

# 22. Required Tests

Add focused tests.

## 22.1 Configuration

Test:

- valid database resilience configuration,
- invalid retry count,
- invalid timeout,
- negative durations,
- startup validation behavior.

---

## 22.2 Transient database failure

Test:

- transient failure retries when safe,
- retries are bounded,
- successful retry succeeds,
- exhausted retry fails.

Use controlled test doubles.

Do not depend on an unreliable real database outage.

---

## 22.3 SQLite busy / locked

Where feasible with the current test environment:

- simulate or classify busy/locked failure,
- verify bounded retry behavior,
- verify cancellation interrupts waiting.

Do not create flaky timing-dependent tests.

---

## 22.4 Optimistic concurrency

Required scenario:

```text
Context A loads entity
Context B loads same entity

Context A updates + saves

Context B updates stale entity + saves

Expected:
ConcurrencyConflictException
No silent overwrite
```

Use a real EF Core provider appropriate for testing.

---

## 22.5 Transaction rollback

Verify a multi-entity transactional operation:

```text
first write succeeds
second write fails

Expected:
first write rolled back
```

Only test a transaction boundary actually introduced.

---

## 22.6 Retry duplicate protection

Verify retry does not create duplicates for at least one important operation such as:

- orchestration run,
- approval request,
- audit event.

Use the actual idempotency mechanism implemented.

---

## 22.7 Metrics

Verify:

- retry counter,
- database failure counter,
- concurrency conflict counter,
- timing aggregation.

---

## 22.8 API

Verify:

- concurrency conflict -> 409 ProblemDetails,
- transient database failure -> sanitized 503,
- no connection string leakage,
- correlation ID preserved.

---

## 22.9 Health checks

Verify:

- liveness remains independent of temporary database readiness,
- readiness reflects database availability,
- failure details are sanitized.

---

## 22.10 Worker composition root

Verify all new database resilience services resolve correctly from Worker DI.

Maintain API/Worker parity where applicable.

---

# 23. Testing Requirements

Run:

```powershell
dotnet build apps/AI.Factory.slnx
```

Then:

```powershell
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

Do not report success until:

- build succeeds,
- no compilation errors,
- no warnings introduced,
- all tests pass.

If a real issue is discovered during implementation, fix it instead of merely documenting it.

Do not weaken or delete existing tests to make the suite pass.

---

# 24. Documentation

Create:

```text
.ai/context/phase-17.4.7-database-resilience-concurrency.md
```

Document:

1. database resilience architecture,
2. retry ownership,
3. provider-specific behavior,
4. optimistic concurrency model,
5. transaction boundary rules,
6. duplicate/idempotency protection,
7. SQLite busy/locked handling,
8. database health checks,
9. metrics,
10. audit events,
11. diagnostics endpoint,
12. API error mapping,
13. worker behavior,
14. extension points,
15. intentionally deferred items.

---

# 25. Explicit Non-Goals

Do NOT implement:

- distributed transactions,
- event sourcing,
- CQRS,
- Kafka,
- database sharding,
- multi-region replication,
- Prometheus,
- Grafana,
- a generic enterprise service bus,
- a second Unit of Work framework,
- a second retry framework competing with existing resilience mechanisms.

Do not rewrite repositories merely for style.

Do not move unrelated code.

---

# 26. Completion Criteria

Phase 17.4.7 is complete only when:

1. database resilience configuration is strongly typed and validated,
2. retry ownership is clearly defined,
3. retry multiplication is avoided,
4. provider-aware transient failures are handled conservatively,
5. SQLite busy/locked conditions are considered,
6. selected mutable entities have optimistic concurrency protection,
7. stale writes cannot silently overwrite newer writes,
8. concurrency conflicts are mapped to application/API semantics,
9. transaction boundaries are explicit where needed,
10. external calls are never held inside long-running DB transactions,
11. retry-sensitive writes are protected from duplicates,
12. database metrics are recorded,
13. database failures are audited safely,
14. diagnostics expose useful non-sensitive information,
15. readiness reflects database availability,
16. liveness remains process-oriented,
17. API and Worker composition roots are updated,
18. migrations are created only when needed,
19. all existing tests remain green,
20. new tests cover the scenarios defined above,
21. `.ai/context/phase-17.4.7-database-resilience-concurrency.md` is created.

---

# Final Output Required

After implementation, provide a complete walkthrough containing:

## 1. Summary of Changes

## 2. Architecture Decisions

Especially explain:

- retry ownership,
- EF execution strategy vs custom retry,
- SQLite busy/locked behavior,
- concurrency token strategy,
- transaction boundaries.

## 3. Files Created

## 4. Files Modified

## 5. Database Migration

If any.

## 6. Optimistic Concurrency Model

Explain which entities are protected and why.

## 7. Retry Safety & Duplicate Protection

Explain the exact mechanism used.

## 8. Failure Classification

Explain database exception mapping.

## 9. Metrics & Audit

List new counters/events.

## 10. Health & Diagnostics

List readiness behavior and endpoints.

## 11. API Error Mapping

Provide mapping table.

## 12. Test Results

Report exact test counts per project.

## 13. Deferred Items

Explicitly list what was intentionally not implemented and why.