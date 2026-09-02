# Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience

## Mission

Implement **Phase 17.4.3 — Circuit Breaker, Bulkhead Isolation & Dependency Resilience** for the AI.Factory system.

This phase follows and MUST integrate with:

- Phase 17.3 — Observability, Structured Logging, Audit Trail & Operational Diagnostics
- Phase 17.4.1 — Global Exception Handling, Problem Details & API Error Contract
- Phase 17.4.2 — Retry, Timeout & Resilience

The goal is to protect AI.Factory from repeatedly calling unhealthy external dependencies and from resource exhaustion caused by too many concurrent dependency operations.

The system must gain:

1. Circuit Breaker support.
2. Bulkhead / concurrency isolation.
3. Dependency-specific resilience profiles.
4. Circuit and bulkhead metrics.
5. Circuit and bulkhead audit events.
6. Correlation-aware operation scopes.
7. API diagnostics for dependency resilience state.
8. Health/readiness integration where appropriate.
9. Safe composition with the existing Retry/Timeout resilience layer.
10. Full automated test coverage.

---

# 0. Mandatory pre-work

Before making ANY code changes:

1. Read `AGENTS.md`.
2. Read all relevant files under:

```text
.ai/context/
```

Especially:

```text
.ai/context/architecture.md
.ai/context/decisions.md
.ai/context/phase-17.3-observability.md
.ai/context/phase-17.4.1-global-exception-handling.md
.ai/context/phase-17.4.2-retry-timeout-resilience.md
```

If filenames differ, locate the equivalent files.

3. Inspect the existing implementations of:

```text
IResilienceExecutor
ResilienceExecutor
ResilienceProfile
ResilienceAgentExecutorDecorator
IFailureClassifier
FailureClassifier
FailureCategory
IAuditLogger
AuditLogger
AuditEventType
IOperationalMetrics
OperationalMetrics
IOperationContext
IOperationContextAccessor
IOperationScopeFactory
CorrelationMiddleware
GlobalExceptionMiddleware
ApiExceptionMapper
CircuitBreakerAgentExecutorDecorator
IAgentExecutor
HermesAgentExecutor
OpenViking integration/client abstractions
HTTP provider clients
API Program.cs
Worker Program.cs
```

4. Search the entire solution before creating any abstraction.

Do NOT create duplicate concepts.

If a circuit breaker abstraction or partial implementation already exists, extend or refactor it rather than creating a competing implementation.

5. Inspect all existing tests and DI composition roots.

6. Before editing each architectural area, identify:

- current callers
- current implementers
- existing abstractions
- persistence impact
- test impact
- whether the change is additive or breaking

---

# 1. Core architectural principles

The implementation MUST follow these rules.

## 1.1 Do not create retry explosions

Phase 17.4.2 already established strict retry ownership.

Circuit breakers and bulkheads must compose with retry without introducing nested retry multiplication.

The desired dependency execution pipeline is conceptually:

```text
Caller
  ↓
Bulkhead
  ↓
Circuit Breaker
  ↓
Retry / Timeout
  ↓
Actual dependency execution
```

However, before changing the existing composition, inspect the current pipeline.

Do not blindly reorder decorators.

Choose the ordering that best preserves:

- fast rejection when the circuit is open
- bulkhead protection
- existing retry ownership
- correct metrics
- correct audit events
- cancellation semantics

Document the final chosen order and WHY.

---

## 1.2 Circuit breakers are dependency-scoped

Do NOT create one global circuit breaker for the entire application.

Each logical dependency must have an independent circuit.

Examples:

```text
AgentExecution
ModelProvider
OpenRouter
OpenViking
ExternalHttpProvider
```

The exact set must be based on existing architecture.

If OpenRouter or OpenViking is not currently active, support configuration for them without requiring a live endpoint.

Do not fabricate provider implementations.

---

## 1.3 Bulkheads are dependency-scoped

A slow or overloaded dependency must not consume unlimited system resources.

Each protected dependency/profile must have independent concurrency limits.

Example:

```text
AgentExecution:
  MaxConcurrentExecutions = 2
  MaxQueueLength = 5

ProviderCall:
  MaxConcurrentExecutions = 10
  MaxQueueLength = 20
```

These are examples only.

Use conservative defaults appropriate to the current system and document them.

---

## 1.4 No external infrastructure

Do NOT introduce:

- Redis
- Kafka
- RabbitMQ
- distributed circuit breaker state
- Kubernetes-specific dependencies
- external resilience databases

This phase is intentionally in-process.

State is local to each API/Worker process.

---

# 2. Circuit Breaker abstraction

Create or extend an abstraction that represents a circuit breaker.

Prefer existing project naming conventions.

Suggested concepts:

```csharp
ICircuitBreaker
ICircuitBreakerRegistry
CircuitBreakerState
CircuitBreakerOptions
CircuitBreakerSnapshot
```

Do NOT create all of these blindly.

Reuse existing abstractions if equivalent concepts already exist.

At minimum, the system must support:

```text
Closed
Open
HalfOpen
```

---

## 2.1 Closed state

Normal operation.

Requests are allowed.

Failures contribute toward opening the circuit.

Successful executions may reset the consecutive failure count according to the selected failure policy.

---

## 2.2 Open state

Dependency is considered unhealthy.

New requests must fail fast.

The underlying dependency MUST NOT be called.

The failure must be represented by a dedicated exception.

Create or reuse something equivalent to:

```csharp
CircuitOpenException
```

The exception must contain enough safe metadata to identify:

- dependency/profile name
- current circuit state
- optional retry-after / next probe time

Do not expose secrets.

---

## 2.3 Half-open state

After the configured break duration expires:

- allow a controlled probe
- do NOT release unlimited traffic immediately

The number of allowed half-open probes must be configurable.

Suggested default:

```text
1
```

If the probe succeeds:

```text
HalfOpen → Closed
```

If the probe fails:

```text
HalfOpen → Open
```

Concurrent probes must be safe.

Do not allow race conditions to accidentally send many simultaneous probes.

---

# 3. Circuit failure policy

The circuit breaker must integrate with:

```text
IFailureClassifier
FailureCategory
```

Do not duplicate failure classification logic.

The breaker should count only failures appropriate for dependency health.

Examples that SHOULD normally contribute:

```text
Provider
Infrastructure
Timeout
```

Transient failures may contribute depending on configuration.

Examples that SHOULD NOT normally open the circuit:

```text
Validation
Approval
Review
Build errors caused by generated code
Test assertion failures
Caller cancellation
```

The exact policy must be explicit and configurable.

Use conservative classification.

Unknown exceptions may contribute depending on the existing failure classification behavior, but do not automatically classify everything as a dependency outage.

---

# 4. Circuit breaker options

Add configuration binding using the project's existing options pattern.

Suggested conceptual configuration:

```json
"CircuitBreakers": {
  "Profiles": {
    "AgentExecution": {
      "Enabled": true,
      "FailureThreshold": 5,
      "SamplingWindow": "00:01:00",
      "BreakDuration": "00:00:30",
      "HalfOpenMaxAttempts": 1,
      "MinimumThroughput": 3
    }
  }
}
```

Do not copy this blindly if Phase 17.4.2 already has a resilience configuration hierarchy.

Prefer integration such as:

```text
Resilience
  Profiles
    AgentExecution
      Retry
      Timeout
      CircuitBreaker
      Bulkhead
```

OR another structure consistent with the existing configuration.

Do not introduce competing configuration roots unless necessary.

---

## 4.1 Options validation

Validate on startup.

Reject invalid values such as:

```text
FailureThreshold <= 0
BreakDuration <= 0
MinimumThroughput <= 0
HalfOpenMaxAttempts <= 0
SamplingWindow <= 0
```

Disabled profiles may permit omitted values if consistent with existing options conventions.

Do not silently accept dangerous configuration.

---

# 5. Bulkhead isolation

Implement dependency-level concurrency isolation.

Suggested abstraction:

```csharp
IBulkheadExecutor
BulkheadOptions
BulkheadRejectedException
BulkheadSnapshot
```

Reuse existing abstractions if they already exist.

The implementation must provide:

1. Maximum concurrent executions.
2. Optional bounded waiting queue.
3. Fast rejection when queue capacity is exceeded.
4. Cancellation-aware waiting.
5. Correct permit release.
6. Thread safety.

A `SemaphoreSlim`-based implementation is acceptable.

Do not introduce a heavyweight dependency unless the repository already uses one.

---

## 5.1 Bulkhead behavior

When concurrency capacity exists:

```text
Request
  ↓
Acquire permit
  ↓
Execute
  ↓
Release permit
```

When concurrency capacity is exhausted:

```text
Request
  ↓
Queue if queue capacity available
  ↓
Wait
```

If queue is also full:

```text
Request
  ↓
Reject immediately
```

Throw a dedicated exception equivalent to:

```csharp
BulkheadRejectedException
```

It must include safe metadata:

- dependency/profile
- max concurrency
- optional queue capacity

Do not leak internal sensitive data.

---

## 5.2 Cancellation

Caller cancellation MUST always win.

If a caller cancels while waiting for bulkhead capacity:

- remove/stop waiting immediately
- do not execute the dependency
- do not count it as a bulkhead failure
- propagate `OperationCanceledException`

Do not convert caller cancellation into:

```text
Timeout
Infrastructure
Circuit failure
Retry exhaustion
```

---

# 6. Dependency resilience executor

Create or extend a unified execution path.

The preferred architecture is conceptually:

```text
IDependencyResilienceExecutor
```

or integration into the existing:

```text
IResilienceExecutor
```

Do NOT create a parallel resilience universe.

Inspect Phase 17.4.2 first.

The final design should allow execution like:

```csharp
await resilienceExecutor.ExecuteAsync(
    profileName,
    operationName,
    operation,
    cancellationToken);
```

with the profile automatically applying:

```text
Bulkhead
Circuit Breaker
Retry
Timeout
Failure Classification
Audit
Metrics
Correlation
```

The exact method signature must follow existing conventions.

---

# 7. Decorator composition

Inspect the existing composition.

Phase 17.4.2 reportedly composes agent execution conceptually as:

```text
ResilienceAgentExecutorDecorator
→ CircuitBreakerAgentExecutorDecorator
→ HermesAgentExecutor
```

Do NOT assume this is still correct.

Inspect the actual code.

Refactor into one clear composition.

Avoid:

```text
Retry
 → Circuit
   → Retry
     → Circuit
```

or duplicate protection.

The final composition must have exactly one owner for:

- retry
- timeout
- circuit breaker
- bulkhead

per execution path.

Document the final pipeline in:

```text
.ai/context/phase-17.4.3-circuit-breaker-bulkhead.md
```

---

# 8. Circuit breaker registry

Circuit state must be shared by operations using the same dependency profile.

Do NOT instantiate a new circuit breaker per request.

Introduce a registry/factory if necessary.

Example conceptual behavior:

```text
AgentExecution
    ↓
Shared Circuit Breaker

ProviderCall
    ↓
Different Shared Circuit Breaker
```

The registry must be thread-safe.

Lifecycle should be appropriate for DI singleton/shared state.

Do not accidentally share all dependencies through one circuit.

---

# 9. Metrics

Extend the existing:

```text
IOperationalMetrics
OperationalMetrics
OperationalMetricsSnapshot
```

Do not create a second metrics system.

Add counters at minimum:

```text
CircuitOpened
CircuitClosed
CircuitHalfOpen
CircuitRejected

BulkheadEntered
BulkheadQueued
BulkheadRejected
BulkheadCompleted
```

Add current-state information where appropriate.

Timing metrics are optional unless useful and supported by real measurements.

Do not fabricate timing data.

All counters must be thread-safe.

Continue using:

```text
Interlocked
```

or the existing concurrency-safe metrics approach.

---

# 10. Audit events

Extend the existing audit model.

Do NOT create a second audit system.

Add event types only if genuinely missing.

Suggested events:

```text
CircuitStateChanged
CircuitRejected
BulkheadEntered
BulkheadQueued
BulkheadRejected
```

Avoid excessive audit noise.

At minimum audit:

### Circuit

```text
Closed → Open
Open → HalfOpen
HalfOpen → Closed
HalfOpen → Open
Request rejected because circuit is open
```

### Bulkhead

Audit rejection.

Queued events may be audited only if this does not create excessive volume.

All audit events must preserve:

```text
CorrelationId
OperationId
ParentOperationId
ProjectId
TaskId
RunId
```

through the existing operation context/audit logger.

Use the existing `AuditLogger` funnel.

Do not bypass it.

---

# 11. Failure classification

Update `FailureClassifier` only if necessary.

Add explicit classification for:

```text
CircuitOpenException
BulkheadRejectedException
```

Recommended behavior:

```text
CircuitOpenException
  Category = Provider or Infrastructure
  IsTransient = true

BulkheadRejectedException
  Category = Infrastructure
  IsTransient = true
```

However, inspect the existing failure category semantics before deciding.

Do not classify these as:

```text
Unknown
Validation
```

The API exception mapper must understand these failures.

---

# 12. API error behavior

Integrate with the existing:

```text
GlobalExceptionMiddleware
ApiExceptionMapper
ProblemDetails
```

Suggested HTTP behavior:

### Circuit open

```text
503 Service Unavailable
```

ProblemDetails should include a stable safe error code such as:

```text
DEPENDENCY_UNAVAILABLE
```

or:

```text
CIRCUIT_OPEN
```

Do not expose stack traces.

If a safe retry duration is known, consider:

```text
Retry-After
```

header.

### Bulkhead rejection

Normally:

```text
429 Too Many Requests
```

or:

```text
503 Service Unavailable
```

Choose one consistent contract and document the reasoning.

Do not use 500.

---

# 13. Dependency diagnostics API

Extend the operational diagnostics without duplicating existing endpoints.

Add a safe endpoint such as:

```text
GET /api/operations/resilience
```

or another route consistent with the existing API.

The response should expose snapshots for configured dependencies.

Example conceptual response:

```json
{
  "dependencies": [
    {
      "name": "AgentExecution",
      "circuitState": "Closed",
      "consecutiveFailures": 0,
      "openedAtUtc": null,
      "nextProbeAtUtc": null,
      "bulkhead": {
        "activeExecutions": 1,
        "queuedExecutions": 0,
        "maxConcurrentExecutions": 2,
        "maxQueueLength": 5
      }
    }
  ]
}
```

Do not expose:

- credentials
- API keys
- prompts
- provider secrets
- internal stack traces

This endpoint should be diagnostic only.

---

# 14. Health and readiness integration

Do not make `/health/live` depend on external dependencies.

Liveness means the process is alive.

For readiness, inspect existing health check behavior.

Circuit state may affect readiness only if the dependency is genuinely required for the host's primary function.

Do NOT make an optional disabled dependency fail readiness.

Suggested behavior:

```text
Disabled dependency
    → Healthy / ignored

Closed circuit
    → Healthy

HalfOpen
    → Degraded

Open circuit for required dependency
    → Unhealthy or Degraded depending on host requirements
```

Follow the existing health architecture.

Do not invent an unrelated health system.

---

# 15. Correlation and operation scopes

Every dependency operation should remain correlated with its parent operation.

Bulkhead waiting and circuit rejection must not lose:

```text
CorrelationId
OperationId
ParentOperationId
ProjectId
TaskId
RunId
```

If a child operation scope is created, it must follow the Phase 17.3 operation hierarchy.

Do not create unrelated correlation IDs.

---

# 16. Concurrency correctness

Pay special attention to race conditions.

Test concurrent scenarios.

At minimum verify:

### Circuit

- concurrent failures do not corrupt state
- only the configured number of half-open probes run
- open circuit rejects without calling dependency
- successful half-open probe closes circuit
- failed half-open probe reopens circuit

### Bulkhead

- max concurrency is never exceeded
- queued requests wait correctly
- queue rejection works
- cancellation while queued releases correctly
- permit is always released after success
- permit is always released after exception
- permit is always released after timeout

---

# 17. Retry interaction

This section is critical.

Phase 17.4.2 introduced retries.

Do NOT allow retries to create unexpected circuit behavior.

Define and test whether the circuit sees:

### Option A

Each failed low-level retry attempt.

OR

### Option B

Only the final exhausted operation failure.

Choose explicitly based on dependency health semantics.

For this architecture, prefer the option that avoids opening a circuit due to one logical operation whose internal retries already recovered.

Document the decision.

Do not leave the interaction implicit.

---

# 18. Agent execution integration

The existing:

```text
IAgentExecutor
HermesAgentExecutor
ResilienceAgentExecutorDecorator
CircuitBreakerAgentExecutorDecorator
```

must be inspected.

Refactor if needed.

The final agent execution path must:

1. Respect bulkhead concurrency.
2. Fail fast when circuit open.
3. Retry only according to the existing Phase 17.4.2 ownership rules.
4. Respect attempt timeout.
5. Respect operation timeout.
6. Preserve caller cancellation.
7. Preserve correlation.
8. Record metrics.
9. Record audit events.

Do not duplicate retries inside:

```text
HermesAgentExecutor
```

unless it already has an explicitly owned retry responsibility.

---

# 19. Provider integration

If OpenViking HTTP clients or model/provider clients exist:

- integrate resilience at the dependency boundary
- use the `ProviderCall` profile
- do not retry or circuit-break business logic
- protect actual network operations

If no real provider implementation exists yet:

- implement the reusable infrastructure
- configure the profile
- add tests using fake delegates/clients

Do not fabricate HTTP clients merely to demonstrate resilience.

---

# 20. Configuration

Update API and Worker configuration consistently.

Follow the existing Phase 17.4.2 resilience configuration style.

Suggested profiles:

```text
AgentExecution
ProviderCall
BuildExecution
TestExecution
RepairExecution
BackgroundOperation
```

Not every profile must necessarily have circuit breaking and bulkhead enabled.

Suggested policy:

### AgentExecution

```text
CircuitBreaker: Enabled
Bulkhead: Enabled
```

### ProviderCall

```text
CircuitBreaker: Enabled
Bulkhead: Enabled
```

### BuildExecution

```text
CircuitBreaker: Disabled
Bulkhead: optional
```

Compiler failures are not dependency health failures.

### TestExecution

```text
CircuitBreaker: Disabled
Bulkhead: optional
```

Test assertion failures are not dependency outages.

### RepairExecution

Usually:

```text
CircuitBreaker: Disabled
```

because repair is semantic orchestration.

### BackgroundOperation

Use only where the operation targets an external/shared dependency.

Do not blindly enable everything.

---

# 21. Dependency injection

Ensure API and Worker composition roots register all required services.

Inspect the existing registration patterns.

Expected lifetimes conceptually:

```text
CircuitBreakerRegistry
    Singleton

BulkheadRegistry
    Singleton

Metrics
    Existing lifetime

Resilience executor
    Existing lifetime

Failure classifier
    Existing lifetime
```

Do not create scoped circuit state.

That would reset the circuit every request.

Add or extend Worker composition root tests to ensure all new abstractions resolve.

---

# 22. Tests

Create comprehensive tests.

Follow the existing test conventions.

Do not skip tests.

## 22.1 Circuit breaker tests

At minimum:

1. Starts closed.
2. Allows successful execution.
3. Opens after configured failures.
4. Rejects while open.
5. Does not invoke underlying operation while open.
6. Moves to half-open after break duration.
7. Allows only configured probe count.
8. Successful probe closes circuit.
9. Failed probe reopens circuit.
10. Failure window behavior works.
11. Non-contributing failures do not open circuit.
12. Concurrent failures are safe.

---

## 22.2 Bulkhead tests

At minimum:

1. Allows up to max concurrency.
2. Additional operations queue.
3. Queue capacity is enforced.
4. Excess operations reject.
5. Queued operation executes when permit released.
6. Cancellation while queued works.
7. Exception releases permit.
8. Timeout releases permit.
9. Max concurrency never exceeded.

---

## 22.3 Integration tests

Test:

```text
Bulkhead
  ↓
Circuit
  ↓
Retry/Timeout
  ↓
Fake dependency
```

Verify:

- retries do not cause duplicate circuit state updates unexpectedly
- circuit open prevents dependency invocation
- bulkhead rejection does not incorrectly open the circuit
- cancellation does not open the circuit
- validation failure does not open the circuit

---

## 22.4 API tests

Verify:

### Circuit open

- returns expected status
- returns `application/problem+json`
- contains correlation ID
- contains safe error code
- no stack trace

### Bulkhead rejection

- returns expected status
- returns valid ProblemDetails
- correlation ID preserved

### Diagnostics endpoint

- returns configured dependency states
- no secrets exposed

---

## 22.5 Metrics tests

Verify:

```text
CircuitOpened
CircuitClosed
CircuitHalfOpen
CircuitRejected

BulkheadEntered
BulkheadQueued
BulkheadRejected
BulkheadCompleted
```

increment correctly.

Verify thread safety where practical.

---

# 23. Database changes

This phase SHOULD NOT require database changes unless genuinely necessary.

Circuit state is intentionally in-process.

Do NOT persist circuit state merely for observability.

Audit events already provide historical information.

If you believe persistence is necessary:

STOP.

Document why before implementing.

Do not invent a migration.

---

# 24. Backward compatibility

Preserve existing behavior where resilience features are disabled.

The following must continue working:

```text
API
Worker
AutonomousCodingOrchestrator
HermesAgentExecutor
Retry/Timeout resilience
Correlation middleware
Global exception handling
Audit logging
Metrics
Health endpoints
Existing diagnostics
```

Do not break existing interfaces unnecessarily.

Prefer:

```text
additive optional parameters
new decorators
new interfaces where no equivalent exists
```

Do not perform unrelated refactors.

---

# 25. Documentation

Create:

```text
.ai/context/phase-17.4.3-circuit-breaker-bulkhead.md
```

Document:

1. Final decorator/execution order.
2. Why that order was selected.
3. Circuit state machine.
4. Failure categories that contribute to circuit opening.
5. Retry/circuit interaction decision.
6. Bulkhead queue behavior.
7. Dependency profiles.
8. Configuration.
9. Metrics.
10. Audit events.
11. API error mapping.
12. Diagnostics endpoint.
13. Health behavior.
14. Worker behavior.
15. Intentional limitations.
16. Future extension points.

Also update relevant architecture/decision documents if required by repository conventions.

---

# 26. Intentional limitations

Explicitly document that this phase does NOT implement:

```text
Distributed circuit breaker state
Redis-backed resilience state
Cross-node bulkheads
Message queues
Dead letter queues
Prometheus exporter
OpenTelemetry exporter
External circuit management
Automatic provider failover
```

Do not add them.

---

# 27. Required verification

Run:

```powershell
dotnet build apps\AI.Factory.slnx
```

Then:

```powershell
dotnet test apps\AI.Factory.slnx --blame-hang-timeout 90s
```

Do not claim success without running both.

If failures occur:

1. Diagnose them.
2. Fix only the actual cause.
3. Re-run the complete test suite.

---

# 28. Final report

After implementation, provide a detailed final report containing:

## 1. Summary

What was implemented.

## 2. Architecture

Final execution/decorator pipeline.

For example:

```text
Caller
  ↓
Bulkhead
  ↓
Circuit Breaker
  ↓
Retry / Timeout
  ↓
Dependency
```

Use the ACTUAL final pipeline.

## 3. Files created

List all new files.

## 4. Files modified

List all modified files and why.

## 5. Circuit breaker behavior

Explain:

```text
Closed
Open
HalfOpen
```

and failure thresholds.

## 6. Bulkhead behavior

Explain:

```text
Max concurrency
Queue
Rejection
Cancellation
Permit release
```

## 7. Retry interaction

Explicitly state whether the circuit sees:

```text
individual retry failures
```

or:

```text
final exhausted operation failures
```

and why.

## 8. Failure policy

Which failure categories contribute to opening a circuit.

## 9. Metrics

All new counters.

## 10. Audit events

All new event types/outcomes.

## 11. API behavior

ProblemDetails mapping for:

```text
CircuitOpenException
BulkheadRejectedException
```

## 12. Configuration

List all resilience profiles and relevant settings.

## 13. Diagnostics

New resilience diagnostics endpoint(s).

## 14. Health behavior

How circuit states affect liveness/readiness.

## 15. Test result

Exact:

```text
total
passed
failed
skipped
```

## 16. Build result

Exact build outcome.

## 17. Intentional limitations

Explicitly list what was deliberately deferred.

---

# Final constraints

DO NOT:

- create duplicate resilience abstractions
- create nested retries
- create one global circuit for all dependencies
- reset circuit state per HTTP request
- count validation failures as dependency outages
- convert caller cancellation into retry/circuit failures
- persist circuit state unnecessarily
- add Redis/Kafka/RabbitMQ
- add external metrics infrastructure
- expose secrets through diagnostics
- silently change existing retry ownership
- fabricate provider integrations

DO:

- inspect the existing architecture first
- reuse existing abstractions
- preserve correlation
- preserve audit conventions
- preserve metrics conventions
- use `IFailureClassifier`
- integrate with existing ProblemDetails handling
- make all state thread-safe
- test concurrency behavior
- document all resilience ownership decisions
- run the complete build and test suite before declaring completion