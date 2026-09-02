# Phase 17.4.2 — Retry, Timeout & Resilience

## Role

You are a senior .NET 10 backend architect and implementation engineer.

You are continuing an existing production-oriented AI Factory system.

Implement **Phase 17.4.2 — Retry, Timeout & Resilience** completely.

The system already has a working orchestration core and Phase 17.3 observability infrastructure.

Do not redesign the architecture.

Do not create duplicate concepts.

Reuse the existing abstractions wherever possible.

---

# 0. Mandatory Pre-Work

Before making ANY code changes:

1. Read `AGENTS.md` completely.
2. Read all relevant files under:
   - `.ai/context/`
3. Read at minimum:
   - `.ai/context/architecture.md`
   - `.ai/context/decisions.md`
   - `.ai/context/phase-17.3-observability.md`
4. Inspect the current implementation of:
   - `AutonomousCodingOrchestrator`
   - `IAgentExecutor`
   - `IModelRouter`
   - `IFailureClassifier`
   - `FailureClassifier`
   - `IOperationContext`
   - `IOperationScopeFactory`
   - `IOperationalMetrics`
   - `IAuditLogger`
   - `AuditLogger`
   - `AuditLogEntry`
   - `AuditEventType`
   - `FailureCategory`
   - `GlobalExceptionMiddleware`
   - Worker execution flow
   - Hermes/OpenRouter/provider adapters
   - Build/Test/Validation execution flow
5. Search the entire solution before creating:
   - retry abstractions
   - timeout abstractions
   - resilience abstractions
   - execution result types
   - retry counters
   - failure categories

Do not assume missing abstractions.

First identify whether equivalent concepts already exist.

---

# 1. Objective

Implement a centralized resilience layer supporting:

1. Retry
2. Exponential backoff
3. Jitter
4. Retry classification
5. Per-attempt timeout
6. Overall operation timeout
7. Cancellation propagation
8. Retry observability
9. Retry audit events
10. Retry metrics
11. Retry exhaustion handling
12. Configurable resilience profiles

The implementation must work for:

- Agent/model execution
- Provider/API calls
- Build execution
- Test execution
- Repair attempts
- Worker/background execution

Do NOT blindly retry everything.

Retry only failures classified as transient or explicitly configured as retryable.

---

# 2. Architecture Rules

The existing architecture already contains:

- `IFailureClassifier`
- `FailureCategory`
- `IOperationContext`
- `IOperationScopeFactory`
- `IAuditLogger`
- `IOperationalMetrics`
- `AutonomousCodingOrchestrator`

These MUST be reused.

Do NOT create another:

- failure classification system
- correlation system
- audit system
- metrics system
- execution context system

The resilience implementation must integrate with the existing infrastructure.

The expected conceptual flow is:

```text
Operation
    |
    v
OperationScope
    |
    v
ResilienceExecutor
    |
    +--> Attempt 1
    |       |
    |       +--> timeout?
    |       +--> success?
    |       +--> failure classification
    |
    +--> Retry Decision
    |
    +--> Delay / Backoff / Jitter
    |
    +--> Attempt 2
    |
    +--> ...
    |
    +--> Success
    |
    +--> OR Retry Exhausted
```

The resilience layer must preserve:

- CorrelationId
- OperationId
- ParentOperationId
- ProjectId
- TaskId
- RunId

Nested retry attempts must remain associated with the parent operation.

Do not generate unrelated correlation IDs for retries.

---

# 3. Core Abstractions

Create only the minimum required abstractions.

Prefer placing reusable abstractions in the existing Application abstraction area.

Suggested types:

```text
IResilienceExecutor
ResilienceOptions
ResilienceProfile
ResilienceExecutionResult
RetryContext
RetryDecision
```

Do not blindly create all of these if equivalent types already exist.

The minimum public API should allow:

```csharp
await resilienceExecutor.ExecuteAsync(
    operationName,
    async cancellationToken =>
    {
        // operation
    },
    cancellationToken);
```

The API must support typed results where needed.

Example:

```csharp
Task<T> ExecuteAsync<T>(
    string operationName,
    Func<CancellationToken, Task<T>> operation,
    ResilienceProfile profile,
    CancellationToken cancellationToken);
```

Do not expose Polly types throughout Application code.

Polly or another resilience implementation must remain an implementation detail.

Application code should depend on project abstractions.

---

# 4. Resilience Profiles

Implement named resilience profiles.

Do not hardcode retry behavior inside:

- `AutonomousCodingOrchestrator`
- `HermesAgentExecutor`
- model adapters
- build executors
- test executors

Suggested profiles:

```text
AgentExecution
ProviderCall
BuildExecution
TestExecution
RepairExecution
BackgroundOperation
```

Each profile should support:

```text
Enabled
MaxRetryAttempts
InitialDelay
MaxDelay
BackoffType
UseJitter
AttemptTimeout
OperationTimeout
RetryTransientFailuresOnly
```

The implementation must validate configuration.

Invalid configurations must fail during startup or options validation rather than causing undefined runtime behavior.

Examples of invalid configuration:

- negative retry count
- zero/negative timeout
- MaxDelay smaller than InitialDelay when applicable
- null profile name
- invalid enum values

---

# 5. Retry Semantics

Be explicit about retry semantics.

Use:

```text
MaxRetryAttempts = number of retries AFTER the initial attempt
```

Therefore:

```text
MaxRetryAttempts = 3
```

means:

```text
Attempt 1
Retry 1
Retry 2
Retry 3
```

Maximum total attempts:

```text
4
```

Document this clearly.

---

# 6. Retry Classification

Retry decisions MUST integrate with the existing:

```text
IFailureClassifier
FailureClassificationResult
FailureCategory
IsTransient
```

Default behavior:

```text
Retry only when:

classification.IsTransient == true
```

Never retry by default:

```text
Validation
Approval
Review
Cancelled
Unknown
```

unless explicit configuration allows it.

The implementation must avoid retrying deterministic failures.

Examples:

### Retryable

Potentially retry:

```text
Provider
Model
Timeout
Infrastructure
Temporary network failure
HTTP transient failure
Rate limiting when appropriate
Temporary external service failure
```

### Not Retryable

Do not retry:

```text
Validation errors
Invalid request
Approval rejection
Business rule violation
Permanent configuration errors
Cancelled operations
Known deterministic build errors
Known deterministic test failures
```

A build/test failure must NOT automatically be retried just because it failed.

Retrying Build/Test requires explicit profile configuration or transient classification.

---

# 7. Timeout Model

Implement two distinct timeout levels.

## 7.1 Attempt Timeout

Each attempt may have:

```text
AttemptTimeout
```

Example:

```text
60 seconds
```

When the attempt exceeds the timeout:

1. cancel that attempt
2. classify the failure
3. emit observability information
4. determine whether retry is appropriate

Attempt timeout must not cancel the entire operation automatically unless configured or required by the overall operation timeout.

---

## 7.2 Overall Operation Timeout

The complete resilience operation may have:

```text
OperationTimeout
```

This includes:

- attempts
- retry delays
- retries

Example:

```text
10 minutes
```

When the overall timeout expires:

```text
stop immediately
do not start another retry
cancel active work
throw/report timeout
```

The caller's cancellation token must always take precedence.

Do not convert caller cancellation into retryable timeout.

---

# 8. Cancellation Rules

Cancellation is critical.

If the caller cancels:

```text
CancellationToken.IsCancellationRequested == true
```

then:

```text
STOP
DO NOT RETRY
DO NOT convert cancellation into infrastructure failure
```

Cancellation must remain distinguishable from timeout.

Use the existing failure classification infrastructure where appropriate.

A timeout caused by the resilience layer and caller cancellation are not the same thing.

Tests must verify this distinction.

---

# 9. Backoff and Jitter

Implement configurable delay strategies.

At minimum support:

```text
Constant
Exponential
```

Exponential retry must support:

```text
UseJitter = true
```

Default recommended behavior for transient remote failures:

```text
Exponential
UseJitter = true
```

Delays must be capped by:

```text
MaxDelay
```

Do not use deterministic exponential delays for every concurrent operation when jitter is enabled.

Do not make tests depend on exact random delay values.

Abstract delay calculation or randomness where necessary so tests remain deterministic.

---

# 10. Retry Context

Each retry attempt must have enough context for diagnostics.

Introduce a lightweight retry context if one does not already exist.

It should expose at minimum:

```text
OperationName
AttemptNumber
MaxAttempts
CorrelationId
OperationId
ProjectId
TaskId
RunId
FailureCategory
IsTransient
LastFailureSummary
```

Do not persist retry context directly as a separate database entity unless existing architecture requires it.

The existing audit trail should be the primary persisted history.

---

# 11. Audit Integration

Reuse:

```text
IAuditLogger
AuditLogEntry
AuditEventType
```

Do not create a second audit pipeline.

Add new audit event types only if genuinely missing.

Recommended events:

```text
RetryScheduled
RetryAttemptStarted
RetryAttemptSucceeded
RetryAttemptFailed
RetryExhausted
OperationTimedOut
```

Do not create redundant events if existing event types already communicate the same thing.

Every retry-related audit entry should include when available:

```text
CorrelationId
OperationId
ParentOperationId
ProjectId
TaskId
RunId
Outcome
ActorType
ActorId
FailureCategory
Duration
```

Suggested outcomes:

```text
Scheduled
Started
Succeeded
Failed
Exhausted
TimedOut
Cancelled
```

Retry audit events must allow an operator to answer:

```text
Why was this operation retried?
How many attempts occurred?
Which attempt succeeded?
Why did retries stop?
Was the final failure transient?
Did timeout occur?
```

---

# 12. Metrics Integration

Reuse:

```text
IOperationalMetrics
```

Do not create a separate metrics subsystem.

Extend the existing snapshot and implementation only as necessary.

Add resilience metrics such as:

```text
RetryAttempts
RetrySucceeded
RetryExhausted
RetryCancelled
AttemptTimeouts
OperationTimeouts
```

If the existing metric model already contains equivalent counters, reuse them.

Optional timing metrics:

```text
RetryDelayDuration
TotalResilienceDuration
```

Do not persist raw metric samples.

Metrics must remain:

```text
thread-safe
in-memory
snapshot based
```

Follow the Phase 17.3 implementation style.

---

# 13. Operation Scope Integration

Retry attempts must integrate with:

```text
IOperationScopeFactory
```

The parent operation should keep its:

```text
CorrelationId
```

Each retry attempt may have a child:

```text
OperationId
```

if that matches the existing operation scope design.

Recommended hierarchy:

```text
AutonomousCodingOperation
    |
    +--> AgentExecution Attempt 1
    |
    +--> AgentExecution Attempt 2
    |
    +--> AgentExecution Attempt 3
```

Each child operation:

```text
inherits CorrelationId
sets ParentOperationId
has its own OperationId
```

Do not create operation scopes if doing so would duplicate existing scope behavior unnecessarily.

---

# 14. Integration Targets

Integrate resilience incrementally.

Do NOT wrap the entire application in a global retry.

At minimum inspect and integrate the following where appropriate.

---

## 14.1 Agent Execution

Inspect:

```text
IAgentExecutor
HermesAgentExecutor
other agent executors
```

Agent execution should use the:

```text
AgentExecution
```

profile.

Requirements:

- transient provider failure can retry
- permanent agent failure does not loop forever
- caller cancellation stops retries
- correlation propagates
- each attempt is auditable
- duration metrics remain accurate

---

## 14.2 Model / Provider Calls

Inspect:

```text
IModelRouter
OpenRouter adapter
provider adapters
HTTP clients
```

Provider calls should use:

```text
ProviderCall
```

profile.

For HTTP calls:

- retry transient failures only
- do not retry invalid requests
- do not retry authentication/authorization failures
- support timeout
- respect cancellation

If the project uses `HttpClientFactory`, use the current .NET resilience approach where appropriate.

Do not introduce the deprecated `Microsoft.Extensions.Http.Polly` package.

Prefer:

```text
Microsoft.Extensions.Http.Resilience
```

for HTTP-specific resilience when it fits the existing architecture.

---

## 14.3 Build Execution

Inspect existing build execution.

Use:

```text
BuildExecution
```

profile.

Default behavior should be conservative.

A compiler error is usually deterministic.

Therefore:

```text
do not blindly retry build failures
```

Only retry when:

- execution infrastructure failed
- process startup failed transiently
- configured explicitly

Build timeout must be supported.

---

## 14.4 Test Execution

Use:

```text
TestExecution
```

profile.

Again:

```text
test assertion failure != transient failure
```

Do not retry ordinary test failures.

Timeout and infrastructure failures may be retryable depending on classification/configuration.

---

## 14.5 Repair Execution

Use:

```text
RepairExecution
```

profile.

Do not accidentally multiply repair loops.

The existing repair attempt count and resilience retry count are different concepts.

Example:

```text
Repair attempt #2
    |
    +--> transient provider retry #1
    +--> transient provider retry #2
```

These must remain distinguishable.

Do not change business-level repair attempt semantics.

---

# 15. Avoid Retry Explosion

This is critical.

The system may contain nested operations:

```text
Orchestrator
    -> Agent
        -> Model Provider
```

Do not allow every layer to independently retry in a way that creates exponential retry multiplication.

Example of a bad design:

```text
Orchestrator retries 3 times
Agent retries 3 times
Provider retries 3 times
```

Potential result:

```text
4 × 4 × 4 = 64 calls
```

Avoid this.

Choose and document retry ownership.

Recommended principle:

```text
Retry closest to the transient failure domain.
```

For example:

```text
HTTP provider call
    -> provider-level retry

Agent execution
    -> retry only agent-level transient failures

Orchestrator
    -> business-level retry only when explicitly intended
```

Do not create retry at every layer by default.

Document the ownership decision.

---

# 16. Exception Model

Do not unnecessarily introduce custom exceptions.

Only introduce types where required to distinguish:

```text
attempt timeout
overall operation timeout
retry exhausted
```

If custom exceptions are introduced:

- preserve the original exception
- preserve inner exception
- preserve failure category where possible
- include attempt information

Do not swallow exceptions.

The final caller must receive meaningful failure information.

---

# 17. Resilience Result

Consider whether a result object is necessary.

If existing architecture is exception-based:

```text
do not force every API to become Result<T>
```

If introducing a resilience result type, it must contain meaningful information:

```text
Succeeded
AttemptsPerformed
RetriesPerformed
Duration
FailureCategory
WasTimedOut
WasCancelled
```

Do not create a duplicate result abstraction if existing execution result types already carry this information.

---

# 18. Configuration

Add resilience configuration to the existing configuration structure.

Suggested structure:

```json
{
  "Resilience": {
    "Profiles": {
      "AgentExecution": {
        "Enabled": true,
        "MaxRetryAttempts": 2,
        "InitialDelay": "00:00:01",
        "MaxDelay": "00:00:15",
        "BackoffType": "Exponential",
        "UseJitter": true,
        "AttemptTimeout": "00:05:00",
        "OperationTimeout": "00:15:00",
        "RetryTransientFailuresOnly": true
      },
      "ProviderCall": {
        "Enabled": true,
        "MaxRetryAttempts": 3,
        "InitialDelay": "00:00:01",
        "MaxDelay": "00:00:10",
        "BackoffType": "Exponential",
        "UseJitter": true,
        "AttemptTimeout": "00:01:00",
        "OperationTimeout": "00:05:00",
        "RetryTransientFailuresOnly": true
      }
    }
  }
}
```

Do not copy this blindly.

Adapt it to existing configuration conventions.

Configuration must support:

```text
API
Worker
tests
development
```

Use options validation.

Do not read configuration manually throughout business logic.

---

# 19. API Diagnostics

Do not create unnecessary production control endpoints.

The existing diagnostics infrastructure should expose enough information to understand resilience behavior.

Extend existing operational diagnostics only where useful.

Possible additions:

```text
GET /api/operations/metrics
```

should include resilience counters automatically through the existing metrics snapshot.

Do not add endpoints for every retry action.

If a useful diagnostics query is added, ensure it follows existing API conventions.

---

# 20. Health Behavior

Do not mark the entire service unhealthy merely because one transient operation fails.

Do not change existing:

```text
/health
/health/live
/health/ready
```

unless the resilience implementation introduces a genuine dependency readiness requirement.

Timeout/retry exhaustion should be visible through:

```text
audit
metrics
diagnostics
```

not automatically converted into process-wide health failure.

---

# 21. Database Changes

Avoid database changes unless genuinely necessary.

Retry history should primarily use the existing audit system.

Do NOT create tables such as:

```text
retry_attempts
resilience_operations
timeout_events
```

unless there is a demonstrated architectural requirement.

Prefer:

```text
AuditLogEntry
+
CorrelationId
+
OperationId
+
EventType
+
Outcome
```

for persistence.

If EF changes are required:

1. create migration
2. inspect generated migration
3. apply migration to development database
4. ensure existing data remains compatible

---

# 22. Dependency Rules

Before adding any NuGet package:

1. search whether the functionality already exists in .NET 10
2. search whether the package is already referenced
3. prefer current Microsoft-supported resilience packages
4. avoid deprecated packages

Do not introduce:

- MediatR
- CQRS
- MassTransit
- RabbitMQ
- Kafka
- Prometheus
- OpenTelemetry collector
- custom event bus

These are outside this phase.

Do not add a package merely to implement a simple delay.

---

# 23. Testing Requirements

Testing is mandatory.

Follow the existing test project structure.

Do not weaken existing tests.

Add tests for at least the following.

---

## 23.1 Retry

Test:

```text
operation succeeds first attempt
operation fails transiently then succeeds
operation fails transiently until retries exhausted
non-transient failure is not retried
```

Verify exact attempt count.

---

## 23.2 Failure Classification Integration

Test:

```text
IsTransient = true -> retry
IsTransient = false -> no retry
Unknown failure -> conservative no retry
```

Do not duplicate FailureClassifier tests.

Test the integration with resilience behavior.

---

## 23.3 Attempt Timeout

Test:

```text
operation exceeds attempt timeout
attempt is cancelled
timeout is classified correctly
retry occurs only if retryable
```

---

## 23.4 Overall Timeout

Test:

```text
multiple attempts
retry delays
overall timeout expires
no additional attempt starts
```

---

## 23.5 Caller Cancellation

Test:

```text
caller cancellation before execution
caller cancellation during attempt
caller cancellation during retry delay
```

Verify:

```text
no retry after caller cancellation
```

---

## 23.6 Backoff

Test:

```text
constant delay calculation
exponential delay calculation
maximum delay cap
```

Do not test exact random jitter values.

Use an abstraction or deterministic random source where needed.

---

## 23.7 Correlation

Test:

```text
retry attempts preserve CorrelationId
nested attempt links ParentOperationId correctly
```

---

## 23.8 Audit

Test:

```text
retry scheduled
attempt started
attempt failed
attempt succeeded
retry exhausted
timeout
```

Verify correlation information is present.

---

## 23.9 Metrics

Test:

```text
retry attempts counter
retry success counter
retry exhausted counter
attempt timeout counter
operation timeout counter
```

Verify thread-safe counters remain correct under concurrent operations where practical.

---

## 23.10 Integration

Add integration tests for at least one real integration path.

Prefer one of:

```text
AgentExecutor
ProviderAdapter
Worker execution
```

The test must prove that resilience is not merely implemented but unused.

---

# 24. Logging

Do not add excessive logs.

Use the existing structured logging conventions.

Retry logs should include:

```text
OperationName
AttemptNumber
MaxAttempts
Delay
FailureCategory
IsTransient
CorrelationId
OperationId
ProjectId
TaskId
RunId
```

Do not log:

- API keys
- provider secrets
- prompts containing secrets
- full credentials
- sensitive configuration

---

# 25. Performance Rules

Do not:

```text
Task.Delay synchronously
Thread.Sleep
block threads
```

Use asynchronous APIs.

Do not create unbounded retries.

Do not allow retry delays to ignore cancellation.

Do not allocate excessive per-attempt state.

---

# 26. Required Documentation

Create:

```text
.ai/context/phase-17.4.2-retry-timeout-resilience.md
```

Document:

1. architecture
2. retry ownership
3. retry classification rules
4. transient vs permanent failures
5. resilience profiles
6. timeout model
7. cancellation behavior
8. retry/backoff/jitter behavior
9. correlation propagation
10. audit events
11. metrics
12. configuration
13. nested retry explosion prevention
14. extension points
15. limitations

Also update relevant architecture/decision documentation if Phase 17.4.2 establishes a new architectural decision.

---

# 27. Explicit Non-Goals

Do NOT implement:

```text
Circuit breaker
Bulkhead isolation
Rate limiting
Hedging
Distributed retries
Persistent retry queues
Message broker retry
Dead letter queues
Prometheus
Grafana
OpenTelemetry collector
Distributed tracing backend
Automatic provider failover
Multi-region failover
```

Those may be future phases.

This phase is specifically:

```text
Retry
+
Timeout
+
Cancellation correctness
+
Observability
+
Resilience integration
```

---

# 28. Required Build Validation

After implementation run:

```powershell
dotnet build apps\AI.Factory.slnx
```

Then:

```powershell
dotnet test apps\AI.Factory.slnx --blame-hang-timeout 90s
```

If an EF migration is created:

```powershell
dotnet ef database update `
  --project apps\src\AI.Factory.Infrastructure `
  --startup-project apps\src\AI.Factory.Api
```

Adapt the command only if the existing repository already uses another EF startup/project convention.

Do not claim success unless the commands actually succeed.

---

# 29. Final Verification Checklist

Before declaring the phase complete, verify:

```text
[ ] Existing build succeeds
[ ] Existing tests remain green
[ ] New tests are added
[ ] Transient failures retry
[ ] Permanent failures do not retry
[ ] Unknown failures are conservative
[ ] Caller cancellation stops retries
[ ] Retry delay supports cancellation
[ ] Attempt timeout works
[ ] Overall timeout works
[ ] Retry attempts preserve correlation
[ ] Retry attempts are auditable
[ ] Metrics are updated
[ ] No duplicate audit system exists
[ ] No duplicate failure classifier exists
[ ] No retry explosion exists
[ ] No unbounded retry exists
[ ] Configuration is validated
[ ] Documentation is updated
```

---

# 30. Final Report Format

After implementation, provide a report with exactly these sections:

## 1. Summary of changes

## 2. Retry ownership decisions

Explain exactly which layer owns retry for:

```text
Agent
Provider
Build
Test
Repair
```

and why nested retry multiplication is avoided.

## 3. Files created

## 4. Files modified

## 5. Resilience profiles

List every implemented profile and effective behavior.

## 6. Timeout behavior

Explain:

```text
Attempt timeout
Operation timeout
Caller cancellation
```

and how they differ.

## 7. Audit and correlation behavior

Explain how retry attempts appear in the audit trail.

## 8. Metrics added

## 9. Configuration

Show only the relevant configuration structure.

Do not expose secrets.

## 10. Test result

Report:

```text
total
passed
failed
skipped
```

## 11. Build result

## 12. Intentional limitations

Explicitly list anything intentionally deferred.

Do not claim a feature is implemented when it is only an abstraction.

Do not fabricate successful test results.

Implement the smallest clean architecture change that fully satisfies this phase.