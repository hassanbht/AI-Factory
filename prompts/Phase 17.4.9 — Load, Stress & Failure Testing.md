# Phase 17.4.9 — Load, Stress & Failure Testing

## Objective

Implement a comprehensive production-oriented load, stress, concurrency, failure-injection, and recovery testing strategy for the AI Factory platform.

This phase must validate the resilience mechanisms implemented in Phases 17.4.1 through 17.4.8 under realistic concurrent and failure conditions.

The goal is not merely to add more unit tests.

The system must be tested as an integrated application under:

- concurrent API requests,
- sustained load,
- burst traffic,
- rate limiting,
- request cancellation,
- timeout scenarios,
- dependency outages,
- circuit breaker transitions,
- bulkhead saturation,
- database contention,
- optimistic concurrency conflicts,
- worker interruption,
- container restart,
- migration restart safety,
- graceful degradation,
- and recovery after failure.

The implementation must preserve Clean Architecture and must not introduce test-only behavior into production execution paths.

---

# 1. Phase Scope

Validate the production behavior of the following previously implemented systems:

## API Reliability

- Global exception handling.
- RFC 7807 ProblemDetails responses.
- Correlation ID propagation.
- Request cancellation.
- Request timeout behavior.
- Rate limiting.
- Request body limits.
- Operational endpoint protection.

## Dependency Resilience

- Retry policies.
- Attempt timeout.
- Operation timeout.
- Circuit breakers.
- Half-open recovery.
- Bulkhead isolation.
- Bounded queues.
- Dependency failure classification.

## Database Resilience

- SQLite busy and locked handling.
- Retry and backoff behavior.
- Optimistic concurrency.
- Transaction rollback.
- Idempotent persistence.
- Concurrent database writes.

## Worker Reliability

- Worker lifecycle.
- Graceful shutdown.
- Cancellation propagation.
- Health state transitions.
- Recovery after restart.

## Deployment Reliability

- Migration runner.
- Persistent volumes.
- Container restart behavior.
- Health checks.
- Startup ordering.
- Production Docker Compose configuration.

---

# 2. Testing Architecture

Create a dedicated testing strategy separating fast tests from expensive resilience and load tests.

The solution should introduce clearly separated categories:

```text
Unit
Integration
Resilience
Load
Stress
FailureInjection
Container
```

Do not run expensive stress tests by default in the normal fast test suite unless explicitly configured.

The default command:

```powershell
dotnet test apps/AI.Factory.slnx
```

must remain deterministic and reasonably fast.

Introduce explicit filtering or dedicated test projects for expensive tests.

Possible structure:

```text
apps/tests/
    AI.Factory.Application.Tests/
    AI.Factory.Api.Tests/
    AI.Factory.Worker.Tests/
    AI.Factory.Resilience.Tests/
```

If a separate project is unnecessary, use explicit test categories/traits, but ensure the test organization remains clear.

---

# 3. Load Test Infrastructure

Create reusable load testing abstractions.

Introduce:

```text
ILoadTestRunner
LoadTestScenario
LoadTestResult
LoadTestMetrics
LoadTestConfiguration
```

The abstractions should support:

- configurable concurrency,
- total request count,
- duration,
- warm-up duration,
- ramp-up,
- cancellation,
- success/failure counting,
- latency measurement,
- percentile calculation,
- throughput calculation.

At minimum collect:

```text
TotalOperations
SuccessfulOperations
FailedOperations
CancelledOperations
RejectedOperations

AverageLatency
MinLatency
MaxLatency

P50Latency
P95Latency
P99Latency

OperationsPerSecond
Duration
```

Latency measurement must use:

```text
Stopwatch.GetTimestamp()
```

or another monotonic timing mechanism.

Do not rely on wall-clock time for latency measurement.

---

# 4. Concurrent API Load Tests

Implement realistic concurrent API tests using the actual API test host.

Test scenarios should include:

## 4.1 Sustained Read Load

Execute concurrent GET requests against representative endpoints.

Validate:

- no unexpected HTTP 500 responses,
- correlation IDs remain isolated,
- response contracts remain valid,
- latency remains bounded,
- no request cross-contamination occurs.

## 4.2 Concurrent Write Load

Execute concurrent create/update operations.

Validate:

- database consistency,
- no duplicate persistence,
- no invalid aggregate state,
- expected conflicts are returned as HTTP 409,
- unexpected database exceptions do not escape as HTTP 500.

## 4.3 Burst Load

Generate a burst significantly above normal concurrency.

Validate:

- rate limiting activates correctly,
- excess requests receive HTTP 429,
- Retry-After is present when applicable,
- API remains responsive,
- health endpoints remain available.

## 4.4 Mixed Workload

Run concurrent:

- project reads,
- project updates,
- orchestration requests,
- operational diagnostics.

Validate that expensive operations do not starve normal requests.

---

# 5. Rate Limiting Stress Tests

Stress the policies implemented in Phase 17.4.5.

Test:

```text
GeneralApi
OperationalApi
ExpensiveOperation
```

Scenarios:

1. Requests below limit.
2. Requests exactly at limit.
3. Requests above limit.
4. Concurrent requests crossing the limit.
5. Window expiration and recovery.
6. Health endpoints remaining accessible during API throttling.

Validate:

- correct HTTP 429 responses,
- RFC ProblemDetails response,
- correlation ID preservation,
- Retry-After behavior,
- metrics increment correctly,
- audit events are recorded safely.

---

# 6. Circuit Breaker Failure Injection

Create deterministic dependency simulators.

Introduce reusable test doubles capable of producing:

```text
Success
TransientFailure
PermanentFailure
Timeout
Cancellation
DelayedSuccess
DelayedFailure
```

Test the complete lifecycle.

## 6.1 Closed → Open

Generate consecutive dependency failures.

Validate:

- failure threshold is reached,
- circuit transitions to Open,
- audit events are recorded,
- metrics increment,
- subsequent calls fail fast.

## 6.2 Open Fast Rejection

Validate that:

- bulkhead permits are not consumed,
- requests do not enter the queue,
- dependency execution is not invoked,
- CircuitOpenException is produced.

## 6.3 Open → HalfOpen

Advance deterministic time beyond break duration.

Do not use Thread.Sleep for long durations.

Introduce a controllable time abstraction if necessary.

Validate:

- circuit enters HalfOpen,
- only configured probe count executes,
- additional probes are rejected.

## 6.4 HalfOpen → Closed

Return successful probe responses.

Validate:

- circuit closes,
- failure count resets,
- normal execution resumes.

## 6.5 HalfOpen → Open

Return a failing probe.

Validate:

- circuit immediately reopens,
- break duration restarts,
- metrics and audit events are recorded.

---

# 7. Bulkhead Stress Tests

Perform concurrent operations against dependencies with deliberately small limits.

Test:

```text
MaxConcurrentExecutions
MaxQueueLength
```

Scenarios:

1. Requests within concurrency capacity.
2. Requests filling the queue.
3. Queue overflow.
4. Cancellation while queued.
5. Permit release after success.
6. Permit release after exception.
7. Permit release after timeout.
8. Permit release after cancellation.

Validate:

- no permit leaks,
- active count returns to zero,
- queued count returns to zero,
- rejected requests receive BulkheadRejectedException,
- HTTP mapping returns 429 where applicable,
- circuit breaker health is not affected by bulkhead rejection.

Add a repeated stress scenario to detect semaphore leaks.

---

# 8. Retry and Timeout Stress Tests

Validate Phase 17.4.2 behavior under repeated failures.

Test:

## Recovered Transient Failure

```text
Failure
Failure
Success
```

Validate:

- retries occur,
- final result succeeds,
- circuit health remains healthy according to Option B,
- metrics reflect retry attempts.

## Exhausted Retry

```text
Failure
Failure
Failure
...
```

Validate:

- retry limit is respected,
- final failure classification is correct,
- circuit breaker receives failure feedback when appropriate.

## Attempt Timeout

Simulate an operation exceeding attempt timeout.

Validate:

- attempt is cancelled,
- retry behavior follows classification,
- resources are released.

## Operation Timeout

Simulate repeated attempts exceeding total operation timeout.

Validate:

- operation stops,
- no further retries start,
- correct timeout classification occurs.

## Cancellation During Retry Delay

Cancel the operation while waiting for backoff.

Validate immediate cancellation and no additional retry attempt.

---

# 9. Database Concurrency Stress Testing

Create realistic concurrent database tests.

## 9.1 Lost Update Scenario

Load the same entity from multiple contexts.

Perform competing modifications.

Validate:

- exactly one conflicting update succeeds,
- conflicting updates produce ConcurrencyConflictException,
- API maps conflict to HTTP 409,
- entity version increases correctly.

## 9.2 Concurrent Transaction Stress

Execute concurrent multi-step approval operations.

Validate:

- workflow state remains consistent,
- no partial transaction persists,
- duplicate state transitions do not occur.

## 9.3 SQLite Busy / Locked Simulation

Create deterministic contention.

Validate:

- busy/locked conditions are classified,
- bounded retry occurs,
- exponential backoff occurs,
- final failure maps safely,
- database diagnostics remain sanitized.

Do not rely only on mocked exceptions.

Where practical, use real SQLite contention scenarios.

## 9.4 Idempotency Under Concurrency

Send duplicate persistence operations concurrently.

Validate:

- duplicate audit events are prevented,
- expected idempotency behavior is preserved,
- database consistency remains correct.

---

# 10. Worker Failure and Recovery Testing

Test the Worker under interruption.

Scenarios:

## 10.1 Graceful Cancellation

Start a long-running operation.

Trigger cancellation.

Validate:

- cancellation propagates,
- no new work begins,
- in-flight operation reaches a defined state,
- worker readiness changes correctly.

## 10.2 Worker Restart

Validate:

1. Worker begins processing.
2. Worker stops.
3. Persistent state remains consistent.
4. Worker starts again.
5. Processing can continue safely.

Ensure no duplicate execution occurs.

## 10.3 Faulted Worker

Inject an unhandled dependency failure.

Validate:

- lifecycle becomes Faulted where applicable,
- health signal reflects failure,
- diagnostics remain safe,
- restart behavior is predictable.

---

# 11. Container Failure Testing

Extend deployment validation beyond smoke tests.

Use Docker Compose scenarios.

## Scenario A — Normal Startup

```text
migrator
    ↓
API
Worker
```

Validate:

- migrations complete first,
- API and Worker start,
- readiness becomes healthy.

## Scenario B — Migration Failure

Force migration failure.

Validate:

- migrator exits unsuccessfully,
- API does not start,
- Worker does not start,
- diagnostics do not leak connection information.

## Scenario C — API Restart

Restart API.

Validate:

- persistent database remains intact,
- migrations are not corrupted,
- readiness returns successfully.

## Scenario D — Worker Restart

Restart Worker during idle and controlled execution.

Validate:

- health files are recreated correctly,
- readiness transitions correctly,
- persistent state remains consistent.

## Scenario E — Dependency Volume Persistence

Stop and recreate containers without deleting volumes.

Validate persisted database data remains available.

---

# 12. Failure Injection Framework

Create reusable failure injection infrastructure.

Suggested abstractions:

```text
FailureInjectionScenario
FailureInjectionDependency
IFailureInjector
FailureInjectionController
```

Supported failures:

```text
Exception
Timeout
Delay
Cancellation
TransientFailure
PermanentFailure
DatabaseBusy
DatabaseLocked
ConnectionFailure
```

Failure injection must:

- be deterministic,
- be isolated to test environments,
- never be enabled accidentally in Production.

Do not introduce a publicly exposed production endpoint for arbitrary failure injection.

---

# 13. Resource Leak and Stability Testing

Repeatedly execute resilience scenarios.

Validate no unbounded growth in:

- active bulkhead executions,
- queued bulkhead requests,
- circuit breaker registry instances,
- database contexts,
- background tasks.

Where practical, verify:

```text
ActiveExecutions == 0
QueuedExecutions == 0
```

after each scenario completes.

Run repeated cycles of:

```text
success
failure
retry
timeout
cancellation
recovery
```

The system must return to a stable state.

---

# 14. Observability Under Load

Validate that resilience mechanisms remain observable.

Under concurrent load and failures verify:

## Metrics

Correctly increment:

- retry metrics,
- timeout metrics,
- circuit metrics,
- bulkhead metrics,
- database retry metrics,
- rate-limit metrics,
- security rejection metrics.

## Audit Events

Verify:

- expected events are recorded,
- duplicate events are prevented where idempotency applies,
- audit persistence failures do not destabilize primary execution.

## Correlation IDs

Under concurrent load verify:

- no correlation ID leaks between requests,
- each request retains its own operation context,
- response correlation ID matches its request context.

---

# 15. Performance Acceptance Criteria

Do not introduce unrealistic absolute performance requirements that depend on CI hardware.

Instead use relative and behavioral acceptance criteria.

The system must demonstrate:

### Stability

- no deadlocks,
- no hangs,
- no unbounded queue growth,
- no leaked bulkhead permits,
- no corrupted persistent state.

### Correct Degradation

During overload:

- requests may be rejected,
- but rejections must be controlled,
- expected 429/503/504 responses must be returned,
- unexpected 500 errors should not occur.

### Recovery

After load or dependency recovery:

- circuits can return to Closed,
- queues drain,
- worker health returns appropriately,
- database operations recover,
- API readiness remains correct.

---

# 16. Diagnostics

Add a safe operational load/resilience diagnostics capability if useful.

Possible endpoint:

```text
GET /api/operations/load
```

The endpoint must expose only aggregate operational data such as:

```text
ActiveOperations
RecentRejections
RecentTimeouts
CircuitStates
BulkheadSnapshots
DatabaseRetryCount
```

It must not expose:

- request payloads,
- secrets,
- connection strings,
- tokens,
- personal data.

Reuse existing operational endpoint security and rate limiting.

Do not duplicate existing resilience or database diagnostics unnecessarily.

---

# 17. Test Execution Strategy

The following must remain the baseline validation:

```powershell
dotnet build apps/AI.Factory.slnx
```

```powershell
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

Add dedicated commands for resilience/load tests where appropriate.

Examples:

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=Stress"
```

```powershell
dotnet test apps/AI.Factory.slnx --filter "Category=FailureInjection"
```

Container validation:

```powershell
docker compose config
```

```powershell
docker compose build
```

```powershell
docker compose up -d
```

Run controlled failure/restart scenarios and verify recovery.

All tests must clean up resources even after failure.

---

# 18. Test Coverage Requirements

Add comprehensive tests for:

## Circuit Breakers

- Closed → Open.
- Open rejection.
- Open → HalfOpen.
- HalfOpen success.
- HalfOpen failure.
- Concurrent probe limits.

## Bulkheads

- concurrency limits,
- queueing,
- rejection,
- cancellation,
- timeout,
- exception permit release,
- repeated stress cycles.

## Retry & Timeout

- recovered failure,
- exhausted retry,
- attempt timeout,
- operation timeout,
- cancellation during backoff.

## Database

- concurrent updates,
- optimistic concurrency,
- SQLite busy,
- SQLite locked,
- transaction rollback,
- idempotency.

## API

- concurrent requests,
- rate-limit bursts,
- correlation isolation,
- ProblemDetails correctness.

## Worker

- cancellation,
- restart,
- lifecycle recovery,
- fault handling.

## Containers

- migration failure,
- API restart,
- Worker restart,
- persistent volume validation.

---

# 19. Clean Architecture Constraints

Maintain strict architecture boundaries.

## Domain

May contain:

- domain state,
- domain events,
- domain invariants.

Must not depend on:

- load testing,
- HTTP,
- Docker,
- EF Core,
- test frameworks.

## Application

Contains:

- resilience abstractions,
- orchestration contracts,
- metrics contracts.

Must not depend on:

- Docker,
- test runners,
- concrete infrastructure.

## Infrastructure

Contains:

- concrete database and dependency implementations.

## API / Worker

Contains:

- composition root,
- HTTP behavior,
- health checks,
- operational diagnostics.

## Tests

Contain:

- failure injection,
- load generation,
- stress scenarios,
- container validation.

Do not add production dependencies solely for test convenience.

---

# 20. Documentation

Create:

```text
.ai/context/phase-17.4.9-load-stress-failure-testing.md
```

Document:

1. Testing architecture.
2. Test categories.
3. Load scenarios.
4. Failure injection strategy.
5. Circuit breaker stress behavior.
6. Bulkhead saturation behavior.
7. Database concurrency tests.
8. Worker interruption tests.
9. Container failure tests.
10. Acceptance criteria.
11. How to execute fast tests.
12. How to execute expensive resilience tests.
13. Known limitations.

---

# 21. Final Validation

Before considering Phase 17.4.9 complete:

1. Build the complete solution.

```powershell
dotnet build apps/AI.Factory.slnx
```

2. Run the complete default test suite.

```powershell
dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s
```

3. Run resilience tests.

4. Run stress/failure-injection tests.

5. Validate Docker Compose.

```powershell
docker compose config
```

6. Build production images.

```powershell
docker compose build
```

7. Run the production stack.

```powershell
docker compose up -d
```

8. Execute controlled restart/failure scenarios.

9. Verify:

```text
No deadlocks
No hung tests
No leaked permits
No unbounded queues
No unexpected HTTP 500 responses during expected overload scenarios
No secret leakage
No persistent state corruption
Correct recovery after dependency failure
Correct recovery after container restart
```

---

# Completion Criteria

Phase 17.4.9 is complete only when:

- Load testing infrastructure exists.
- Concurrent API scenarios are tested.
- Rate limiting is tested under burst traffic.
- Circuit breakers are tested under repeated failure and recovery.
- Bulkheads are tested under saturation.
- Retry and timeout behavior is tested under load.
- Database concurrency is tested using realistic contention where practical.
- Worker interruption and restart scenarios are tested.
- Container failure and restart scenarios are validated.
- Metrics, audit events, and correlation IDs remain correct under concurrency.
- No resource leaks or deadlocks are observed.
- Production behavior degrades gracefully under overload.
- Recovery behavior is verified.
- Build succeeds.
- All baseline tests pass.
- Dedicated resilience and stress tests pass.
- Docker production validation passes.
- Documentation is created.

Do not stop after adding tests that merely compile.

Execute the relevant test suites and provide a final walkthrough containing:

1. Architecture changes.
2. New testing infrastructure.
3. Load scenarios executed.
4. Failure scenarios executed.
5. Circuit breaker results.
6. Bulkhead results.
7. Database concurrency results.
8. Worker recovery results.
9. Container recovery results.
10. Metrics and observability validation.
11. Exact test counts and results.
12. Any limitations or remaining risks.