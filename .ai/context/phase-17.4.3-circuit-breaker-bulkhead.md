# Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience

## Overview

Phase 17.4.3 introduces comprehensive dependency resilience to the AI Factory platform, providing:
1. **Circuit Breakers**: Protecting external dependencies (e.g. LLM providers, agents, external APIs) from cascading failures by failing fast during outages.
2. **Bulkhead Isolation**: Limiting concurrent resource usage per dependency with bounded queueing to prevent thread and memory starvation.
3. **Unified Execution Pipeline**: Integrated directly inside `ResilienceExecutor` according to Clean Architecture:
   `Caller -> Circuit Breaker Fast-Check -> Bulkhead -> Circuit Breaker Half-Open Gate -> Retry / Timeout -> Dependency Execution`.
4. **Option B Retry & Circuit Breaker Interaction**: Transient errors recovered by retries do not degrade circuit health. Only exhausted retries or non-retryable dependency faults trip the circuit.
5. **Observability & Diagnostics**: Operational metrics, structured audit events, health checks, and `GET /api/operations/resilience`.

---

## Architecture & Components

### 1. Unified Execution Pipeline in `ResilienceExecutor`
- **Step 1: Circuit Breaker Fast-Check**: If the dependency's circuit is `Open` and break duration has not elapsed, fail fast immediately by throwing `CircuitOpenException`. Does not consume bulkhead permits or wait in queue.
- **Step 2: Bulkhead Isolation**: Acquires concurrency permit using `SemaphoreSlim`. If active slots are saturated, queues up to `MaxQueueLength`. If queue capacity is exceeded, immediately throws `BulkheadRejectedException` (mapped to HTTP 429).
- **Step 3: Circuit Breaker Half-Open Gate**: If circuit is in `HalfOpen` probe state, admits up to `HalfOpenTrialCount` trial requests. Additional concurrent attempts are rejected fast.
- **Step 4: Retry / Timeout Attempt Loop**: Executes attempt loop with backoff and timeouts. Observes final logical operation outcome:
  - Success -> `circuit.RecordSuccess()`.
  - Recovered transient failure -> Logical success, circuit health preserved (Option B).
  - Permanent failure or retry exhaustion on `Provider`, `Infrastructure`, or `Timeout` -> `circuit.RecordFailure()`.

### 2. Abstractions (`AI.Factory.Application.Resilience`)
- `CircuitState`: Enum (`Closed`, `Open`, `HalfOpen`).
- `CircuitBreakerProfileOptions`: Configuration for `Enabled`, `FailureThreshold`, `BreakDuration`, `HalfOpenTrialCount`.
- `CircuitBreakerSnapshot`: Point-in-time state of a circuit breaker.
- `ICircuitBreaker`: Interface defining `CanExecuteFast()`, `TryEnter()`, `RecordSuccess()`, `RecordFailure()`, `GetSnapshot()`.
- `ICircuitBreakerRegistry`: Registry managing circuit breaker instances by dependency name.
- `CircuitOpenException`: Thrown when circuit is open or trial limit reached.
- `BulkheadProfileOptions`: Configuration for `Enabled`, `MaxConcurrentExecutions`, `MaxQueueLength`.
- `BulkheadSnapshot`: Point-in-time state of bulkhead concurrency and queues.
- `IBulkhead`: Interface defining `AcquireAsync(CancellationToken)`, `GetSnapshot()`.
- `IBulkheadRegistry`: Registry managing bulkhead instances by dependency name.
- `BulkheadRejectedException`: Thrown when bulkhead concurrency and queue limits are exceeded.

### 3. Profiles & Default Configuration
- **AgentExecution**: CB(threshold: 5, break: 30s, trials: 1), Bulkhead(maxConcurrent: 2, maxQueue: 5).
- **ProviderCall**: CB(threshold: 5, break: 30s, trials: 1), Bulkhead(maxConcurrent: 10, maxQueue: 20).
- **BuildExecution**: CB disabled, Bulkhead(maxConcurrent: 2, maxQueue: 5).
- **TestExecution**: CB disabled, Bulkhead(maxConcurrent: 2, maxQueue: 5).
- **RepairExecution**: CB disabled, Bulkhead(maxConcurrent: 1, maxQueue: 2).
- **BackgroundOperation**: CB disabled, Bulkhead(maxConcurrent: 5, maxQueue: 10).

### 4. Diagnostics & Error Mapping
- **`GET /api/operations/resilience`**: Returns array of all monitored dependencies with circuit state, consecutive failures, probe timestamps, and active/queued bulkhead executions.
- **`CircuitBreakerHealthCheck`**: Readiness check that reports `Degraded` if any dependency circuit is `Open` or `HalfOpen`, and `Healthy` when all are closed.
- **`ApiExceptionMapper`**: Maps `BulkheadRejectedException` to `429 Too Many Requests` (`too_many_requests`).

---

## Verification
- Total tests passing: **419** tests across the entire solution (Domain, Application, Infrastructure, Worker, Api).
- Zero compilation errors, zero warnings, and clean DI lifecycle validation.
