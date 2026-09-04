# Phase 17.4.10 — Production Risk Register

## Overview

This document establishes the formal Production Risk Register for the AI Factory platform, evaluating architectural, operational, resiliency, security, and deployment risks across the system.

Severity Rating Scale:
- **Critical**: System outage, data corruption, secret leakage, or unrecoverable state loss. Blocks production deployment immediately.
- **High**: Significant operational degradation, localized availability loss under heavy load, or manual intervention required for recovery.
- **Medium**: Known functional limitation, acceptable performance impact under stress, or mitigated edge condition.
- **Low**: Minor observational gap, test harness limitation, or cosmetic logging anomaly.

---

## Risk Inventory

### RISK-01: External AI Provider Dependency Outage (OpenRouter / LLM APIs)
- **Title**: Sustained External AI Provider Unavailable or Degraded
- **Description**: External LLM providers (e.g. OpenRouter, Anthropic, OpenAI) experience severe latency, HTTP 502/503/504 errors, or complete network outages, causing AI agent invocations to fail repeatedly.
- **Affected Component**: `AI.Factory.Application.Resilience` (`ResilienceExecutor`, `CircuitBreakerRegistry`, `OpenRouterClient`)
- **Likelihood**: High
- **Impact**: High
- **Severity**: High
- **Current Mitigation**:
  - Poly-layered resilience pipeline: Circuit Breaker with Option B half-open probe gating, exponential backoff retries with jitter (default max 3 attempts), per-attempt and total operation timeouts.
  - Open circuits fail fast immediately (`503 Service Unavailable`, `service_unavailable`) without consuming bulkhead execution permits or starving callers.
  - Failures classified into transient vs permanent; permanent failures (e.g. 401 Unauthorized, invalid request) fail immediately without wasteful retries.
- **Remaining Mitigation**: Multi-provider fallback routing (failover to secondary provider endpoint when primary circuit opens).
- **Owner / Responsible Area**: AI Infrastructure / Application Resilience
- **Production Blocking?**: No
- **Decision**: Accept with current mitigation. Circuit breaker and bulkhead protections verified in Phase 17.4.3 & Phase 17.4.9 stress tests.

---

### RISK-02: SQLite Database Write Contention & Lock Escalation
- **Title**: Concurrent Database Writes Triggering Database Locked / Busy Under Peak Load
- **Description**: SQLite enforces database-level write serialization. Concurrent writes from multiple worker operations and API requests can compete for the write lock, causing contention, `SQLITE_BUSY` (code 5) or `SQLITE_LOCKED` (code 6).
- **Affected Component**: `AI.Factory.Infrastructure.Persistence` (`DatabaseResiliencePolicy`, `FactoryDbContext`)
- **Likelihood**: Medium
- **Impact**: Medium
- **Severity**: Medium
- **Current Mitigation**:
  - WAL (Write-Ahead Logging) and `Cache=Shared` configured on SQLite connection strings, enabling concurrent readers alongside single writers.
  - Native SQLite busy timeout set to 5000ms.
  - `DatabaseResiliencePolicy` with exponential backoff retries on transient busy/locked conditions.
  - `ApiExceptionMapper` mapping unabsorbed busy/locked errors to `503 Service Unavailable` (`database_unavailable`) with audit logging, preventing internal crashes.
- **Remaining Mitigation**: Production workload recommendation: migrate to PostgreSQL when horizontal API scaling or high-throughput write workloads exceed single-node SQLite capacity.
- **Owner / Responsible Area**: Database Infrastructure
- **Production Blocking?**: No
- **Decision**: Accept for single-node deployment profile. Verified under load/stress testing in Phase 17.4.9.

---

### RISK-03: SQLite Single-Instance & Horizontal Scaling Limitations
- **Title**: Multi-Instance Horizontal Scaling Prohibited by Shared SQLite File
- **Description**: Running multiple container instances of API or Worker accessing the same SQLite file over networked filesystems (NFS/SMB) can cause database corruption or severe locking anomalies due to lack of distributed file lock support.
- **Affected Component**: Deployment Architecture / Database
- **Likelihood**: High (if multi-replica deployed without database migration)
- **Impact**: Critical
- **Severity**: High
- **Current Mitigation**:
  - Docker Compose configuration enforces single-instance deployments (`deploy: replicas: 1`) using local persistent Docker volumes (`ai_factory_production_data`).
  - Strict documentation in operational runbooks prohibiting horizontal scaling of SQLite containers.
- **Remaining Mitigation**: Introduce PostgreSQL provider implementation before configuring multi-replica API or Worker deployments.
- **Owner / Responsible Area**: Infrastructure / DevOps
- **Production Blocking?**: No (for single-node production deployment). Documented as operational constraint.
- **Decision**: Accept for initial single-node release with explicit deployment constraints.

---

### RISK-04: Circuit Breaker Real-Time Dependency in Automated Test Suite
- **Title**: Circuit Breaker Tests Depend on Real Clock Delays
- **Description**: Circuit breaker tests rely on short real wall-clock delays (`Task.Delay`) rather than an injectable virtual/controllable clock abstraction (`TimeProvider`), potentially introducing flakiness on resource-constrained CI agents.
- **Affected Component**: `AI.Factory.Application.Tests.Stress.CircuitBreakerStressTests`
- **Likelihood**: Low
- **Impact**: Low
- **Severity**: Low
- **Current Mitigation**:
  - Test delay intervals are sized with safe safety margins; tests consistently pass across all runs without flakiness.
  - Circuit breaker implementation uses `Stopwatch` / `DateTime.UtcNow` monotonic checks for fail-fast behavior.
- **Remaining Mitigation**: Adopt .NET `TimeProvider` in future refactoring phase to decouple timing logic from system clock entirely.
- **Owner / Responsible Area**: Application Architecture / Quality Engineering
- **Production Blocking?**: No
- **Decision**: Accept as known test harness limitation. Does not affect production runtime behavior.

---

### RISK-05: Bulkhead Permit Saturation Under Traffic Spike
- **Title**: Bulkhead Queue Saturation and Request Rejection Under Burst Load
- **Description**: Heavy simultaneous caller spikes can saturate the execution semaphore and waiting queue, rejecting requests with HTTP 429.
- **Affected Component**: `AI.Factory.Application.Resilience.BulkheadRegistry`
- **Likelihood**: Medium
- **Impact**: Medium
- **Severity**: Medium
- **Current Mitigation**:
  - Separate bulkhead limits configured per dependency partition (e.g. AI provider, Database).
  - Bulkhead rejection throws `BulkheadRejectedException`, cleanly mapped by `ApiExceptionMapper` to `429 Too Many Requests` (`too_many_requests`).
  - Permit releases guaranteed by `try / finally` blocks across exceptions, timeouts, and cancellations (exhaustively proven in `BulkheadStressTests`).
  - Rejected requests do not degrade circuit health.
- **Remaining Mitigation**: Expose dynamic bulkhead tuning via runtime configuration without restart.
- **Owner / Responsible Area**: Application Resilience
- **Production Blocking?**: No
- **Decision**: Accept with current mitigation. Behavior is deliberate backpressure protection.

---

### RISK-06: Production Configuration & Missing Secret Resolution
- **Title**: Missing or Invalid Secrets Triggering Silent Runtime Failures
- **Description**: Running in production with missing `OPENROUTER_API_KEY`, database connection strings, or operational keys could lead to partial failures during runtime if not validated at startup.
- **Affected Component**: `AI.Factory.Infrastructure.Security` / Startup Validation
- **Likelihood**: Low
- **Impact**: High
- **Severity**: Medium
- **Current Mitigation**:
  - Strongly typed configuration options validated on application startup.
  - `SecretManagement:FailFastOnMissingSecret=true` configured in `appsettings.Production.json`.
  - Applications fail fast during boot if required secrets cannot be resolved.
  - Sanitized error reporting ensures missing secret keys are identified by name without leaking secret values or environment variables.
- **Remaining Mitigation**: Add cloud secret manager provider (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault) for enterprise deployments.
- **Owner / Responsible Area**: Security & Infrastructure
- **Production Blocking?**: No
- **Decision**: Mitigated. Verified in Phase 17.4.6 startup validation tests.

---

### RISK-07: Worker Interruption & Stale Lease Recovery Mid-Execution
- **Title**: Hard Worker Termination Leaves In-Flight Orchestration Runs in Orphaned State
- **Description**: A power failure, hard SIGKILL, or host kernel panic terminates the Worker process while executing a long-running AI workflow, leaving the run in `Running` status with an unreleased lease.
- **Affected Component**: `AI.Factory.Worker` (`OrchestrationRunWorker`, `WorkflowRecoveryHostedService`)
- **Likelihood**: Medium
- **Impact**: High
- **Severity**: Medium
- **Current Mitigation**:
  - Graceful shutdown handles `SIGTERM` / `SIGINT` by stopping new claims immediately, cancelling in-flight operations with timeout, and cleanly releasing leases back to `Pending` status.
  - In an ungraceful hard termination, `WorkflowRecoveryHostedService` executes periodic stale lease sweeps (`RecoverStaleLeasesAsync`), reclaiming expired leases and transitioning them back to `Pending` exactly once.
  - Recovery tests (`WorkerRestartRecoveryTests`) verify that a second worker instance recovers orphaned runs without double-execution or lost state.
- **Remaining Mitigation**: Checkpoint intermediate subtask execution states so resumed workflows can restart from the last successful subtask rather than the beginning of the step.
- **Owner / Responsible Area**: Worker Core / Orchestration
- **Production Blocking?**: No
- **Decision**: Mitigated and verified.

---

### RISK-08: Database Migration Failure During Deployment
- **Title**: Failed EF Core Migration Leaves Database Inconsistent or Blocks Startup
- **Description**: A corrupted migration script, schema incompatibility, or permission error causes the migration runner to fail, potentially corrupting existing schema.
- **Affected Component**: `AI.Factory.Infrastructure.Persistence` / Migrator Service
- **Likelihood**: Low
- **Impact**: Critical
- **Severity**: High
- **Current Mitigation**:
  - Two-phase container deployment: dedicated `migrator` service runs `dotnet ef database update` to completion prior to starting API and Worker containers.
  - `docker-compose.yml` enforces `depends_on: migrator: condition: service_completed_successfully`. If migration fails, neither API nor Worker starts.
  - Migrations execute within database transactions where supported.
- **Remaining Mitigation**: Implement automated pre-migration database snapshot backup script in container startup workflow.
- **Owner / Responsible Area**: Database Infrastructure / DevOps
- **Production Blocking?**: No
- **Decision**: Mitigated by architecture. Disaster recovery runbook provides manual rollback procedures.

---

### RISK-09: Worker Dependency Degradation & Health Check Accuracy
- **Title**: Sustained External Dependency Failures Unobserved by Worker Health Probes
- **Description**: If external dependencies fail repeatedly during polling, the worker may remain silently stuck or fail silently without orchestrator awareness.
- **Affected Component**: `AI.Factory.Worker` (`WorkerHealthCoordinator`, `OrchestrationRunWorker`)
- **Likelihood**: Low (post-Phase 17.4.10)
- **Impact**: Medium
- **Severity**: Low
- **Current Mitigation**:
  - Resolved in Phase 17.4.10: `WorkerLifecycleState.Degraded` introduced.
  - Bounded degradation tracking: consecutive poll failure tracking with configurable threshold (default 3).
  - Single transient failure remains `Ready`; 3 consecutive failures transition state to `Degraded` (`IsLive: true`, `IsReady: false`), creating `worker.degraded` probe file and removing `worker.ready`.
  - Successful poll resets failure counters and automatically restores `Ready`.
  - Liveness maintained throughout degradation to prevent unnecessary container churn by orchestrators.
- **Remaining Mitigation**: None required.
- **Owner / Responsible Area**: Worker Lifecycle / Observability
- **Production Blocking?**: No
- **Decision**: Fully resolved and tested in Phase 17.4.10.

---

### RISK-10: Manual Container Failure Scenario Verification vs Automated CI
- **Title**: Docker Compose Multi-Container Restart Scenarios Documented via Runbook Rather Than Automated Integration Test
- **Description**: Container recovery scenarios (A through E in Phase 17.4.9) are validated via documented procedures and Docker Compose commands rather than fully automated Testcontainers xUnit suites, requiring manual execution during major release gates.
- **Affected Component**: CI/CD Pipeline & Quality Assurance
- **Likelihood**: Low
- **Impact**: Low
- **Severity**: Low
- **Current Mitigation**:
  - Reproducible, automated docker scripts provided in production runbook.
  - Docker Compose syntax and container image builds verified directly (`docker compose config`, `docker compose build`).
  - Unit and integration tests comprehensively validate worker recovery semantics at the service and repository boundary.
- **Remaining Mitigation**: Implement Testcontainers-dotnet suite in CI for automated container lifecycle testing once CI runners support Docker-in-Docker.
- **Owner / Responsible Area**: DevOps / Quality Assurance
- **Production Blocking?**: No
- **Decision**: Accept as operational runbook procedure.

---

## Risk Summary Matrix

| Risk ID | Title | Component | Likelihood | Impact | Severity | Production Blocking? | Decision |
|---|---|---|---|---|---|---|---|
| RISK-01 | AI Provider Dependency Outage | Resilience | High | High | High | No | Mitigated (Circuit/Retry/Timeout) |
| RISK-02 | SQLite DB Write Contention | Persistence | Medium | Medium | Medium | No | Mitigated (WAL/Retry/503 Safe) |
| RISK-03 | SQLite Multi-Instance Scale Limitation | Infrastructure | High | Critical | High | No | Accepted (Single-node profile) |
| RISK-04 | Circuit Breaker Real-Time Clock Dependency | Testing | Low | Low | Low | No | Accepted (Test harness limitation) |
| RISK-05 | Bulkhead Permit Saturation | Resilience | Medium | Medium | Medium | No | Mitigated (Backpressure/429) |
| RISK-06 | Missing Secret Resolution | Security | Low | High | Medium | No | Mitigated (Startup Fail-Fast) |
| RISK-07 | Worker Interruption / Orphaned Runs | Worker | Medium | High | Medium | No | Mitigated (Lease sweeps/Drain) |
| RISK-08 | Migration Runner Failure | Deployment | Low | Critical | High | No | Mitigated (Startup Ordering gate) |
| RISK-09 | Worker Health Degradation Tracking | Worker | Low | Medium | Low | No | Resolved in Phase 17.4.10 |
| RISK-10 | Container Scenarios Manual Runbook | QA/DevOps | Low | Low | Low | No | Accepted (Documented Runbook) |

**Conclusion**: Zero unresolved Critical risks. All High risks have active architectural mitigations and documented operating boundaries.
