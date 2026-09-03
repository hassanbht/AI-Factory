# Phase 17.4.7 — Database Resilience & Concurrency

## Overview

Phase 17.4.7 hardens database persistence across AI Factory to make it production-ready under transient database failures, provider locks, concurrent mutations, duplicate command executions, and race conditions without leaking credentials, connection strings, or internal SQL.

---

## 1. Database Resilience Architecture

The database resilience architecture follows Clean Architecture:
- **Domain Layer (`AI.Factory.Domain.Common` & `Entities`)**:
  - `IVersionedEntity`: Pure Domain interface with `long Version { get; set; }`.
  - Implemented by mutable aggregates: `Project`, `WorkItem`, `ApprovalRequest`, `OrchestrationRun`.
  - No dependencies on EF Core, ASP.NET Core, or database drivers.
- **Application Layer (`AI.Factory.Application`)**:
  - `DatabaseResilienceOptions`: Strongly typed configuration (`EnableRetries`, `MaxRetryAttempts`, `InitialRetryDelay`, `MaxRetryDelay`, `EnableOptimisticConcurrency`, `CommandTimeout`).
  - `ConcurrencyConflictException`: Safe domain/application representation of lost updates / stale writes.
  - `IDatabaseResiliencePolicy`: Contract for provider-neutral transient failure classification and safe retry loops.
  - `IDatabaseTransactionManager`: Contract for atomic, short-lived transactional units of work.
  - `IDatabaseDiagnosticsService`: Contract for gathering safe operational database metrics.
  - `IOperationalMetrics`: In-memory thread-safe counters and duration tracking.
- **Infrastructure Layer (`AI.Factory.Infrastructure.Persistence`)**:
  - `DatabaseResiliencePolicy`: Classifies provider exceptions (e.g. SQLite Busy/Locked/IOERR) and manages bounded exponential backoff with jitter.
  - `DatabaseTransactionManager`: Implements short-lived transactions on `FactoryDbContext` with automatic rollback and metric recording.
  - `DatabaseDiagnosticsService`: Gathers safe diagnostic reports for operational queries.
  - `FactoryDbContext`: Configures `.Property(x => x.Version).IsConcurrencyToken()`, automatically increments `Version` for modified versioned entities, and translates `DbUpdateConcurrencyException` into `ConcurrencyConflictException`.
- **API and Worker Composition Roots**:
  - Validates options at startup, registers identical singleton/scoped services, configures EF Core command timeout, and exposes `GET /api/operations/database`.

---

## 2. Retry Ownership & Avoiding Retry Multiplication

The system strictly enforces distinct layers of retry ownership:
1. **Database Layer (`IDatabaseResiliencePolicy`)**:
   - Owns transient database error retries (e.g., SQLite Busy/Locked, connection dropouts) for safe read operations and idempotent executions.
   - Concurrency conflicts (`DbUpdateConcurrencyException` / `ConcurrencyConflictException`) are **NEVER** automatically retried without reloading state; they fail fast with HTTP 409.
2. **External Dependency Layer (`ResilienceExecutor`)**:
   - Owns retries, timeouts, circuit breaking, and bulkheading for external network dependencies (LLMs, OpenRouter, Hermes agent subprocesses, Docker, OpenViking).
3. **API Layer**:
   - Never retries non-idempotent HTTP mutations (e.g. POST/PUT endpoints) at the HTTP layer.

---

## 3. Provider-Specific Behavior

- **SQLite**:
  - `SqliteErrorCode == 5` (`SQLITE_BUSY`): Another connection holds an exclusive lock. Treated as transient and retried with backoff.
  - `SqliteErrorCode == 6` (`SQLITE_LOCKED`): Table lock conflict. Treated as transient and retried.
  - `SqliteErrorCode == 10` (`SQLITE_IOERR`): Temporary file lock or disk buffer glitch. Treated as transient.
- **PostgreSQL / Relational Providers**:
  - Connection failures and transient network drops (`DbException.IsTransient == true`) are classified as transient and retried.
  - Command timeout is propagated via `sqliteOptions.CommandTimeout((int)options.CommandTimeout.TotalSeconds)`.

---

## 4. Optimistic Concurrency Model

### Protected Entities
1. **`Project`**: Prevents concurrent project name/status modifications from overwriting each other.
2. **`WorkItem`**: Prevents competing schedulers or workers from double-claiming or silently overriding state transitions.
3. **`ApprovalRequest`**: Prevents race conditions where two operators make contradictory approval decisions simultaneously.
4. **`OrchestrationRun`**: Prevents race conditions between worker heartbeat updates, progress reporters, and API cancellation requests.

### Mechanism
- Each protected entity implements `IVersionedEntity` with `long Version { get; set; } = 1;`.
- Configured in EF Core via `.Property(x => x.Version).IsConcurrencyToken()`.
- On `SaveChanges` / `SaveChangesAsync`, `FactoryDbContext` increments `Version` for all modified `IVersionedEntity` records.
- If another process updated the record in the interim, EF Core generates `WHERE Id = @p0 AND Version = @pOriginal` which finds 0 rows, throwing `DbUpdateConcurrencyException`.
- `FactoryDbContext` catches `DbUpdateConcurrencyException` and throws `ConcurrencyConflictException`.

---

## 5. Transaction Boundary Rules

- **Short-Lived Transactions Only**: Transactions must only encompass database mutations, never external agent executions, Docker commands, or LLM network requests.
- **Automatic Rollback**: Managed via `IDatabaseTransactionManager` (`DatabaseTransactionManager`). On any exception, `transaction.RollbackAsync()` is called and `RecordDatabaseTransactionFailure()` is recorded.
- **Nested Transactions**: If an ambient transaction is already open (`_db.Database.CurrentTransaction != null`), the manager participates in it rather than opening competing nested transactions.
- **Applied in `ApprovalService`**: Wraps the approval decision update and workflow status transition in an atomic transaction boundary.

---

## 6. Duplicate & Idempotency Protection

- **Audit Logging**: `AuditLogRepository.RecordAsync` performs an idempotency check on `entry.Id`. If an audit entry with the same ID already exists (e.g. during an operation retry), it returns cleanly without duplicating rows or throwing a primary key violation.
- **Run and Task IDs**: Operations use pre-allocated GUIDs (`RunId`, `ExecutionId`, `CorrelationId`) ensuring operations can be identified deterministically across retries.

---

## 7. SQLite Busy / Locked Handling

- SQLite file locks can cause `SQLITE_BUSY` (code 5) or `SQLITE_LOCKED` (code 6) under concurrency.
- `DatabaseResiliencePolicy` detects these error codes and performs bounded exponential backoff with randomized jitter.
- The `CommandTimeout` configured via `DatabaseResilienceOptions` ensures queries do not wait indefinitely.
- Cancellation tokens are checked on every retry iteration, ensuring shutdown or client disconnects interrupt waiting immediately.

---

## 8. Database Health Checks

- **Readiness (`DatabaseHealthCheck` on `/health/ready`)**:
  - Uses `_db.Database.CanConnectAsync(cancellationToken)`.
  - Reports `Healthy` when the database is reachable.
  - Reports `Unhealthy` when unreachable, sanitizing error details via `SensitiveDataSanitizer` to avoid leaking database paths or connection parameters.
- **Liveness (`/health/live`)**:
  - Independent of database connectivity; verifies process liveness.

---

## 9. Operational Metrics

The following database metrics are tracked via `IOperationalMetrics`:
- `DatabaseOperations`: Total database operations executed.
- `DatabaseFailures`: Total failed operations.
- `DatabaseRetries`: Number of transient retries performed.
- `DatabaseTransientFailures`: Total transient failures observed.
- `DatabaseConcurrencyConflicts`: Total optimistic concurrency conflicts detected.
- `DatabaseBusyFailures`: SQLite busy lock encounters.
- `DatabaseLockedFailures`: SQLite locked table encounters.
- `DatabaseTransactionFailures`: Total transaction rollbacks.
- `AverageDatabaseOperationDurationMs`: Running average latency of database operations.

---

## 10. Audit Events

Added to `AuditEventType`:
- `DatabaseRetry`: Logged when an operation retries after transient failure.
- `DatabaseConcurrencyConflict`: Logged when an optimistic concurrency conflict occurs.
- `DatabaseTransactionFailed`: Logged when a database transaction fails and is rolled back.
- `DatabaseOperationFailed`: Logged when a database operation fails permanently.

---

## 11. Diagnostics Endpoint

Endpoint: `GET /api/operations/database`
- **Security**: Protected by `OperationalEndpointSecurityMiddleware` and rate-limited under `OperationalApi`.
- **Payload (`DatabaseDiagnosticsSnapshot`)**:
  - `ProviderName`: Active provider (e.g. "Sqlite").
  - `IsReady`: Boolean connectivity status.
  - `CommandTimeoutSeconds`: Configured command timeout.
  - `RetriesEnabled` & `MaxRetryAttempts`: Active retry policy settings.
  - `OptimisticConcurrencyEnabled`: Concurrency tracking status.
  - Counters: Operations, failures, retries, concurrency conflicts, busy/locked counts, transaction failures, and average latency.
- **Zero Leakage Guarantee**: Completely omits connection strings, file paths, passwords, and SQL query text.

---

## 12. API Error Mapping

| Exception | HTTP Status | ProblemDetails Title | Error Code | Category |
|---|---|---|---|---|
| `ConcurrencyConflictException` | 409 Conflict | Resource was modified | `concurrency_conflict` | Validation |
| `DbUpdateConcurrencyException` | 409 Conflict | Resource was modified | `concurrency_conflict` | Validation |
| `SqliteException` (busy/locked) | 503 Service Unavailable | Database unavailable | `database_unavailable` | Infrastructure |
| Transient `DbException` | 503 Service Unavailable | Service unavailable | `service_unavailable` | Infrastructure |
| Database `TimeoutException` | 504 Gateway Timeout | Operation timed out | `timeout` | Timeout |
| General `DbUpdateException` | 500 Internal Server Error | Database error | `database_error` | Infrastructure |

---

## 13. Worker Behavior

- `AI.Factory.Worker/Program.cs` registers identical database resilience options, `IDatabaseResiliencePolicy`, `IDatabaseTransactionManager`, and `IDatabaseDiagnosticsService`.
- Worker execution loops use cancellation tokens from `IApplicationShutdown`.
- Concurrency conflicts during work claiming fail deterministically without corrupting work item queues.

---

## 14. Extension Points

- **PostgreSQL / Distributed Database**: `IDatabaseResiliencePolicy` can be extended with PostgreSQL-specific transient error codes (e.g., connection reset, deadlock detection `40P01`).
- **Cloud Managed Databases**: Future cloud database providers (e.g. AWS Aurora, Azure SQL) plug in via `IDatabaseResiliencePolicy` and EF Core execution strategy configurations without modifying domain business logic.

---

## 15. Intentionally Deferred Items

- **Distributed Transactions / 2PC**: Distributed transactions are unnecessary for the current architecture and introduce excessive operational complexity.
- **CQRS / Event Sourcing**: Out of scope; existing relational models with audit trailing are deterministic and recoverable.
- **Third-Party Resilience Frameworks**: Using external frameworks (e.g. Polly, MediatR) was avoided to preserve the clean, deterministic native architecture established across Phase 17.
