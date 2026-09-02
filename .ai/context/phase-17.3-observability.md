# Phase 17.3 — Observability, Structured Logging, Audit Trail & Operational Diagnostics

## Architecture

Everything here is in-process — no Prometheus, Grafana, ELK, Jaeger, Kafka, or OpenTelemetry collector. Four new pieces compose with the existing audit trail (from an earlier phase) rather than duplicating it:

- **`IOperationContext`/`IOperationContextAccessor`/`IOperationScopeFactory`** (Application/Abstractions) — operation correlation, AsyncLocal-based, no ASP.NET/EF dependency.
- **`IOperationalMetrics`** (Application/Abstractions) — thread-safe in-process counters/timers, snapshot-only, no raw samples persisted.
- **`IFailureClassifier`** (Application/Abstractions) — conservative exception/reason → `FailureCategory` classification.
- **`IOperationalDiagnosticsService`** (Infrastructure/Persistence) — read model composing existing repositories + the audit trail.

The pre-existing `AuditLogEntry`/`IAuditLogger`/`IAuditLogRepository` (added in an earlier phase) were **extended in place**, not replaced — see "Audit event model" below.

## Correlation flow

1. `IOperationScopeFactory.Begin(name, correlationId?, projectId?, taskId?, runId?)` opens an `IOperationScope`, pushing an `OperationContext` onto an `AsyncLocal` via `OperationContextAccessor`.
2. If no `correlationId` is given, it inherits the currently-open parent's (if any), else a new one is generated — nested scopes automatically share one `CorrelationId` and link via `ParentOperationId`.
3. `IAuditLogger.LogAsync` reads the ambient `OperationContext` (via `IOperationContextAccessor`) when its own `correlationId`/`operationId`/`runId`/`projectId`/`workItemId` parameters are omitted, falling back to `executionId` for `CorrelationId` if no scope is open. This means **every existing `_auditLogger.LogAsync` call site got correlation for free** — none needed to change.
4. Entry points that open a scope:
   - `CorrelationMiddleware` (Api) — one scope per HTTP request; reads/generates `X-Correlation-ID`, returns it in the response header.
   - `AutonomousCodingOrchestrator.ExecuteWorkItemAsync` — wraps the real logic (renamed `ExecuteWorkItemCoreAsync`) in one scope per attempt, `correlationId: executionId` (the pre-existing per-attempt id), `projectId`/`taskId` from the WorkItem.
5. `Fail(exception)`/`Fail(reason)` on the scope marks it FAILED before `DisposeAsync` logs SUCCEEDED/FAILED + `DurationMs` and restores the parent context.

## Audit event model

The pre-existing `AuditLogEntry` entity gained new nullable columns rather than a parallel `AuditEvent` entity (avoids duplicating an equivalent concept):

`CorrelationId`, `OperationId`, `ParentOperationId`, `RunId`, `Outcome`, `ActorType` (default `"System"`), `ActorId`, `FailureCategory`.

The existing `AuditEventType` enum gained `ReviewResult`, `EvaluationResult`, `LearningResult` — the only three lifecycle categories the prompt required that had no audit coverage at all (Review/Evaluation/Learning previously only reached the progress-reporter/trace-service, never the audit trail).

`IAuditLogger.LogAsync` gained trailing optional params (`correlationId`, `operationId`, `runId`, `outcome`, `actorType`, `actorId`, `durationMs`) — additive, so all pre-existing call sites (which use named args) compile unchanged.

## Failure classification

`IFailureClassifier` is deliberately **distinct** from the pre-existing `PipelineFailureCategory`/`OrchestrationFailureCategory` enums — those classify already-known pipeline *outcomes*; `IFailureClassifier` classifies raw exceptions or free-text reasons (with an optional category hint) into the new `FailureCategory` enum (`Unknown, Validation, Build, Test, Agent, Model, Provider, Timeout, Cancelled, Infrastructure, Approval, Review`). Conservative: anything unrecognized is `Unknown`, never guessed.

## Metrics

`OperationalMetrics` (singleton) tracks the exact counters/timings the prompt specifies via `Interlocked` operations — no locks, no persisted samples. `IOperationalMetrics.RecordAgentExecution/RecordBuildExecution/RecordTestExecution/RecordRepairAttempt` are called from `AuditLogger.LogAsync`'s single funnel point (mapped from `AuditEventType`/`AuditSeverity` on `AgentCompleted`/`BuildResult`/`TestResult`/`RepairAttempt`) — no other call site needed to change. `RecordApproval` is called explicitly from `ApprovalService.ApproveAsync`/`RejectAsync` instead, since `HumanApproval`'s severity alone can't distinguish "requested" from "approved" (both `Info`).

**Disclosed limitation**: `BuildDuration`/`TestDuration`/`RepairDuration` have no cheap precise per-call timing source in `AutonomousCodingOrchestrator` (`IValidationEngine.ValidateAsync` measures build+test together in one call — see Phase 17.2's disclosed simplification) and are not wired; the snapshot reports `null` average/total for them rather than a fabricated value. `AgentExecutionDuration` is real (`AgentExecutionResult.DurationMs`).

## Health endpoints

`/health`, `/health/live`, `/health/ready` already existed (with a real `DatabaseHealthCheck`) — unchanged by this phase.

## API endpoints (new)

- `GET /api/operations/metrics` → `OperationalMetricsSnapshot`
- `GET /api/operations/recent?count=` → recent `AuditLogEntry` list
- `GET /api/operations/{correlationId}` → `CorrelationSummary` (404 if nothing recorded)
- `GET /api/projects/{projectId}/diagnostics` → `ProjectDiagnosticSummary` (404 if project doesn't exist)

`GET /api/orchestration-runs/{id}` and `GET /api/audit-log` are unchanged.

## Background workers

`AI.Factory.Worker`'s `ConfigureServices` registers the same `IOperationScopeFactory`/`IOperationalMetrics`/`IFailureClassifier`/`IOperationalDiagnosticsService`/`IOperationContextAccessor` as the Api, mirroring the existing Api/Worker DI convention. No `HttpContext` dependency anywhere in the correlation mechanism, so `AutonomousCodingOrchestrator`'s own scope (opened in `ExecuteWorkItemAsync`) works identically whether triggered from the Worker's `OrchestrationRunWorker` background loop or the autonomous scheduler.

## Extension points

- A future OpenTelemetry exporter could subscribe to `IOperationScopeFactory`'s STARTED/SUCCEEDED/FAILED `ILogger` calls without touching any Application-layer code.
- `IOperationalDiagnosticsService` is a thin read model; a future analytics need should get its own service rather than growing this one.
- `FailureCategory` values were kept intentionally small — extend only when a genuinely new failure shape appears, not speculatively.
