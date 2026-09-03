# Phase 17.4.4 — Graceful Shutdown, Cancellation Propagation & Background Worker Reliability

## Overview

Phase 17.4.4 introduces comprehensive graceful shutdown, cancellation propagation, in-flight operation tracking, and background worker reliability across the AI Factory platform:
1. **Application Shutdown Coordination**: Centralized, thread-safe `IApplicationShutdown` abstraction linked to `IHostApplicationLifetime.ApplicationStopping`, supporting deterministic shutdown initiation, linked cancellation tokens, and async waiting.
2. **Stop Accepting New Work**: When shutdown is initiated, background loops immediately cease polling, workers stop claiming pending jobs, orchestrators reject new tasks, and recovery services skip scheduled sweeps.
3. **In-Flight Operation Tracking**: Thread-safe `IBackgroundOperationTracker` coupled with ambient `IOperationScope` and `OperationContext`, maintaining real-time visibility into active operations and coordinating async draining.
4. **Cancellation Propagation**: Cancellation tokens (`StoppingToken`) propagate down through orchestrator calls, agent execution, tool execution, build/validation tasks, and backoff delays.
5. **Deterministic State Handling (Option B)**: Interrupted runs and work items cleanly release their worker lease and revert status to `Pending` / `Ready` with audit tracking, preventing artificial retry exhaustion and ensuring immediate resumption upon service restart.
6. **API Traffic Gating & Diagnostics**: `/health/ready` transitions to `Unhealthy` on shutdown while `/health/live` remains `Healthy`. Incoming API mutations receive `503 Service Unavailable` with `Retry-After: 30`, while safe diagnostic reads (`GET /api/operations/active`) remain accessible.
7. **Observability**: Structured audit events (`ShutdownInitiated`, `WorkerStopping`, `WorkerStopped`, `OperationsDrained`, `OperationsCancelled`, `ShutdownTimedOut`, `ShutdownRequestRejected`) and operational metrics.

---

## Architecture & Components

### 1. Application Layer (`AI.Factory.Application.Abstractions`)
- **`IApplicationShutdown`**:
  - `bool IsStopping { get; }`: Indicates if shutdown is active.
  - `CancellationToken StoppingToken { get; }`: Propagated token cancelled when shutdown initiates.
  - `bool TryBeginShutdown()`: Idempotent thread-safe trigger.
  - `Task WaitForShutdownAsync(CancellationToken)`: Awaits shutdown initiation.
- **`IBackgroundOperationTracker`**:
  - `int ActiveCount { get; }`: Current active operations in flight.
  - `IReadOnlyList<ActiveOperationInfo> GetActiveOperations()`: Active metadata snapshot.
  - `IDisposable Track(OperationContext)`: Registers operation; handle disposal unregisters.
  - `Task<bool> WaitForDrainAsync(TimeSpan, CancellationToken)`: Awaits zero active operations or timeout.
- **`ShutdownOptions`**:
  - `GracefulTimeoutSeconds` (default: 30): Max time to wait for in-flight tasks to drain.
  - `ForceCancellationAfterSeconds` (default: 25): Threshold before force-cancelling in-flight operations.
  - `Validate()`: Ensures positive values and `ForceCancellationAfterSeconds <= GracefulTimeoutSeconds`.
- **`IOperationScopeFactory`**: Automatically registers active operation scopes with `IBackgroundOperationTracker` on `Begin` and unregisters on disposal.
- **`IOperationalMetrics`**: Tracks shutdown lifecycle, drained/cancelled counts, timeouts, and rejected requests.

### 2. Infrastructure Layer (`AI.Factory.Infrastructure.Shutdown` & `Persistence`)
- **`ApplicationShutdownCoordinator`**: Singleton implementation of `IApplicationShutdown`. Hooks into `IHostApplicationLifetime.ApplicationStopping`, thread-safe state transition, linked cancellation source, and asynchronous audit logging via `IServiceScopeFactory`.
- **`BackgroundOperationTracker`**: In-memory `ConcurrentDictionary<Guid, ActiveOperationInfo>` for lock-free reads and async drain signaling using `TaskCompletionSource`.
- **`OrchestrationRunRepository.ReleaseLeaseAsync(Guid runId)`**: Reverts claimed/running run back to `Pending` and clears lease upon shutdown interruption.

### 3. Worker Layer (`AI.Factory.Worker`)
- **`OrchestrationRunWorker`**:
  - Checks `_shutdown.IsStopping` before polling or claiming pending runs.
  - Links execution cancellation token with `_shutdown.StoppingToken`.
  - Reverts interrupted run leases to `Pending` on shutdown cancellation.
  - Overrides `StopAsync` to coordinate graceful drain via `_tracker.WaitForDrainAsync(...)`, auditing `WorkerStopping`, `OperationsDrained`, `ShutdownTimedOut`, and `WorkerStopped`.
- **`WorkflowRecoveryHostedService`**: Observes `_shutdown.IsStopping` and terminates background sweep loops cleanly.

### 4. API Layer (`AI.Factory.Api`)
- **`ShutdownHealthCheck`**: Readiness check (`"ready"` tag) that returns `Unhealthy` once `_shutdown.IsStopping` is true.
- **`ShutdownMiddleware`**: Intercepts requests during shutdown. Whitelists `/health/*` and `/api/operations/active`, returning `503 Service Unavailable` with `Retry-After: 30` and ProblemDetails for other requests.
- **`GET /api/operations/active`**: Exposes active operation count, stopping state, and non-sensitive operation metadata (IDs, names, start timestamps, duration).

---

## Configuration

In `appsettings.json` (Api and Worker):
```json
"Shutdown": {
  "GracefulTimeoutSeconds": 30,
  "ForceCancellationAfterSeconds": 25
}
```

---

## Verification

- Total tests passing: **448** tests across the entire solution (Worker.Tests: 32, Application.Tests: 354, Api.Tests: 62).
- Zero compilation errors, zero warnings.
- 100% test pass rate with full test suite executing in under 10 seconds.
