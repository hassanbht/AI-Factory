# Phase 17.4.2 — Retry, Timeout & Resilience

## 1. Architecture

Phase 17.4.2 introduces a centralized, profile-driven resilience framework residing in the Application layer (`AI.Factory.Application.Resilience`) that integrates directly with existing Phase 17.3 observability (correlation scopes, audit trail, and operational metrics) and clean architecture boundaries.

```text
Caller (e.g. AgentExecutor, ProviderClient, Service)
    │
    ▼
IResilienceExecutor.ExecuteAsync(operationName, action, profile?, cancellationToken)
    │
    ├── 1. Profile Resolution & Options Validation (ResilienceOptions)
    ├── 2. Overall Operation Timeout (OperationTimeoutException)
    │
    ├── Attempt Loop (1 .. MaxAttempts = 1 + MaxRetryAttempts)
    │     ├── Check caller cancellation (immediate abort without retry)
    │     ├── Check overall timeout (immediate abort without retry)
    │     │
    │     ├── Open Child IOperationScope ($"{operationName}.Attempt")
    │     │     └── Inherits CorrelationId, sets ParentOperationId, generates OperationId
    │     │
    │     ├── Audit: AuditEventType.Retry, Outcome: "Started"
    │     ├── Attempt Timeout Guard (AttemptTimeoutException)
    │     │
    │     ├── Execute Action Func
    │     │     ├── Success ──> Audit "Succeeded", RecordRetrySuccess (attempt > 1), return result
    │     │     ├── Caller Cancelled ──> Audit "Cancelled", RecordRetryCancelled, propagate
    │     │     ├── Attempt Timeout ──> RecordAttemptTimeout, classify as Timeout
    │     │     └── Exception ──> Classify via IFailureClassifier
    │     │
    │     ├── Evaluate Retry Eligibility:
    │     │     ├── attempt < MaxAttempts?
    │     │     ├── IsTransient == true (or in RetryableCategories)?
    │     │     └── Category not in {Validation, Approval, Review, Cancelled, Unknown}?
    │     │
    │     ├── IF ELIGIBLE:
    │     │     ├── Calculate Delay (Constant / Exponential + Jitter up to MaxDelay)
    │     │     ├── RecordRetryAttempt, Audit "Scheduled"
    │     │     ├── Structured Log (Operation, Attempt, MaxAttempts, DelayMs, Category)
    │     │     └── DelayAsync(delay, effectiveToken)
    │     │
    │     └── IF NOT ELIGIBLE:
    │           ├── If attempts exhausted: RecordRetryExhausted, Audit "Exhausted", throw RetryExhaustedException
    │           └── If permanent failure: Fail scope, propagate exception immediately
    │
    ▼
Result / Exception
```

## 2. Retry Ownership & Nested Retry Explosion Prevention

To prevent exponential multiplication of retries across nested layers (e.g. $4 \times 4 \times 4 = 64$ calls), retries are owned exclusively by the layer closest to the transient failure domain:

| Layer / Component | Owned Profile | Max Retries | Rationale |
|---|---|---|---|
| **HTTP Provider / Client** | `ProviderCall` | 3 (4 total) | Retries transient network glitches, 429 rate limits, and 503 gateway outages at the HTTP transport boundary without repeating agent prompt generation. |
| **Agent Executor** | `AgentExecution` | 2 (3 total) | Retries transient container/process startup failures or transient daemon communication errors without restarting the entire orchestration task. |
| **Build Executor** | `BuildExecution` | 0 (default) | Compiler errors (e.g. CS0103, CS0246) are deterministic; ordinary build failures are never retried. Only transient infrastructure/process startup failures retry if explicitly configured. |
| **Test Executor** | `TestExecution` | 0 (default) | Test assertion failures are deterministic; failing tests are never retried. Only process/harness infrastructure faults retry if configured. |
| **Orchestrator** | Business Repair Loop | Bounded by WorkItem `MaxAttempts` (3) | Orchestrator drives semantic repair cycles (generating repair context, calling new models, modifying code). It does NOT multiply transient retries. |

## 3. Failure Classification Rules

Resilience decisions integrate directly with `IFailureClassifier`:
- **Transient Failures (Retryable)**:
  - `TimeoutException`, `AttemptTimeoutException` -> `FailureCategory.Timeout`, `IsTransient = true`
  - `System.IO.IOException` -> `FailureCategory.Infrastructure`, `IsTransient = true`
  - `System.Net.Http.HttpRequestException` -> `FailureCategory.Provider`, `IsTransient = true`
  - `CircuitOpenException` -> `FailureCategory.Provider`, `IsTransient = true`
  - Un-thrown agent failures with hint `"timeout"` or `"infrastructure"` -> `IsTransient = true`
- **Permanent Failures (Non-Retryable)**:
  - `ValidationException`, `ArgumentException`, `NotFoundException`, `ConflictException` -> `FailureCategory.Validation`, `IsTransient = false`
  - `UnauthorizedAccessException`, `ForbiddenException`, `SecurityViolationException` -> `FailureCategory.Infrastructure`, `IsTransient = false`
  - Any unknown/unrecognized exception -> `FailureCategory.Unknown`, `IsTransient = false` (conservative)
  - `OperationCanceledException` when caller cancelled -> `FailureCategory.Cancelled`, never retried.

## 4. Resilience Profiles

Profiles are configured in `ResilienceOptions` and validated on startup:

| Profile Name | Enabled | Max Retries | Initial Delay | Max Delay | Backoff | Jitter | Attempt Timeout | Operation Timeout |
|---|---|---|---|---|---|---|---|---|
| `AgentExecution` | `true` | 2 | 1s | 15s | Exponential | `true` | 5m | 15m |
| `ProviderCall` | `true` | 3 | 1s | 10s | Exponential | `true` | 1m | 5m |
| `BuildExecution` | `true` | 0 | 1s | 5s | Constant | `false` | 3m | 5m |
| `TestExecution` | `true` | 0 | 1s | 5s | Constant | `false` | 5m | 10m |
| `RepairExecution` | `true` | 1 | 2s | 10s | Exponential | `true` | 5m | 15m |
| `BackgroundOperation` | `true` | 3 | 1s | 30s | Exponential | `true` | 2m | 10m |

Validation ensures:
- Profile names are not null or empty.
- `MaxRetryAttempts >= 0`.
- `InitialDelay >= TimeSpan.Zero`.
- `MaxDelay >= InitialDelay`.
- `AttemptTimeout > TimeSpan.Zero` and `OperationTimeout > TimeSpan.Zero`.
- `AttemptTimeout <= OperationTimeout`.

## 5. Timeout Model

Two independent timeout tiers are enforced:
1. **Attempt Timeout (`AttemptTimeout`)**:
   - Bounds the duration of a single execution attempt.
   - If exceeded, cancels the attempt CTS, classifies the failure as `Timeout` (`IsTransient = true`), records `AttemptTimeouts` metric, and allows retry if within `MaxRetryAttempts`.
2. **Overall Operation Timeout (`OperationTimeout`)**:
   - Bounds the total wall-clock time across all attempts and intervening backoff delays.
   - If exceeded, stops all work immediately, records `OperationTimeouts` metric, audits `AuditEventType.Timeout` (`Outcome = "TimedOut"`), and throws `OperationTimeoutException`.

## 6. Cancellation Behavior

- If the caller's cancellation token fires (`cancellationToken.IsCancellationRequested == true`):
  - Execution stops immediately.
  - No retries are scheduled.
  - The failure is never converted into a timeout or infrastructure fault.
  - The `RetryCancelled` metric is incremented.
  - An audit entry with `Outcome = "Cancelled"` is recorded.
  - `OperationCanceledException` is propagated directly to the caller.

## 7. Backoff and Jitter

- Supported strategies: `BackoffType.Constant` and `BackoffType.Exponential`.
- Exponential delay formula: $\min(\text{InitialDelay} \times 2^{\text{attempt} - 1}, \text{MaxDelay})$.
- Jitter: Applies $\pm 20\%$ randomized scaling around the capped delay, bounded between 0 and `MaxDelay`.
- `IBackoffCalculator` and `IDelayProvider` interfaces allow deterministic and zero-delay testing without wall-clock waits.

## 8. Correlation Propagation

- Every retry attempt opens a child `IOperationScope` via `IOperationScopeFactory.Begin($"{operationName}.Attempt", parentCorrelationId, ...)`.
- The attempt scope inherits `CorrelationId`, `ProjectId`, `TaskId`, and `RunId`.
- The attempt scope automatically sets `ParentOperationId` pointing to the parent operation scope's `OperationId`.
- Every audit entry recorded during retry inherits the ambient correlation and operation metadata.

## 9. Audit Events

Resilience events use existing `AuditEventType.Retry` and `AuditEventType.Timeout`:
- `Outcome = "Started"`: Attempt begins.
- `Outcome = "Scheduled"`: Transient failure occurred, retry delay scheduled with details.
- `Outcome = "Succeeded"`: Attempt completed successfully.
- `Outcome = "Failed"`: Attempt failed.
- `Outcome = "Exhausted"`: All retries failed, `RetryExhaustedException` thrown.
- `Outcome = "TimedOut"`: Attempt or overall operation timeout fired.
- `Outcome = "Cancelled"`: Caller cancellation observed.

## 10. Metrics

The `IOperationalMetrics` snapshot exposes thread-safe counters updated via atomic `Interlocked` operations:
- `RetryAttempts`: Total retry attempts performed (attempts > 1).
- `RetrySucceeded`: Retries that resulted in success.
- `RetryExhausted`: Operations where all retry attempts failed.
- `RetryCancelled`: Operations cancelled by the caller.
- `AttemptTimeouts`: Count of individual attempt timeouts.
- `OperationTimeouts`: Count of overall operation timeouts.

## 11. Extension Points

- **Custom Delays**: Implement `IBackoffCalculator` for specialized backoff algorithms (e.g. polynomial, stepped).
- **Custom Time Provider**: Implement `IDelayProvider` for virtual time / testing.
- **New Profiles**: Add new profile configurations under `"Resilience:Profiles"` in `appsettings.json`.

## 12. Intentional Limitations

- No distributed retry coordination (in-process only).
- No message broker / dead letter queue integration.
- No Prometheus / OpenTelemetry collector exporter (counters are exposed via in-process `IOperationalMetrics` and `GET /api/operations/metrics`).
