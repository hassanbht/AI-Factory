# Phase 17.2 — Orchestration Run Observability and Execution Trace

You are continuing work on the existing AI Factory repository.

Repository root:

D:\Project\AI-Factory

Before making any changes, inspect the current implementation.

This phase follows Phase 17.1, which audited architecture wiring and DI.

The repository already has:

- OrchestrationRun
- OrchestrationService
- OrchestrationRunWorker
- OrchestrationRunExecutionEngine
- AutonomousCodingOrchestrator
- Model routing
- Hermes agent execution
- Build/test/repair
- Independent code review
- Human approval
- Evaluation
- Learning
- Worker composition root tests

The Phase 17.1 audit confirmed that the primary orchestration flow is wired and reachable.

DO NOT redesign the architecture.

DO NOT create a parallel execution entity if OrchestrationRun already represents the execution lifecycle.

DO NOT introduce a second orchestration system.

==================================================
PRIMARY GOAL
==================================================

Extend the existing OrchestrationRun lifecycle with persistent execution observability.

The existing OrchestrationRun must remain the canonical representation of one autonomous orchestration execution.

This phase must add a persistent, ordered execution trace attached to OrchestrationRun.

The system must allow developers and future UI consumers to answer:

1. What is this orchestration run currently doing?
2. Which stages have completed?
3. Which stage failed?
4. How long did each stage take?
5. How many attempts occurred?
6. Which model/provider was used when available?
7. Which agent/executor was used when available?
8. What failure information occurred?
9. What is the ordered history of the execution?

==================================================
ARCHITECTURAL CONSTRAINT
==================================================

Do not create a new root Execution aggregate if OrchestrationRun already owns execution lifecycle state.

Instead, inspect the current Domain model and add the smallest coherent child entity/value object structure necessary.

A suggested name is:

OrchestrationRunStep

or another name consistent with existing repository naming conventions.

The implementation must belong naturally to the existing OrchestrationRun aggregate/lifecycle.

==================================================
TRACE MODEL
==================================================

Create persistent execution step records associated with OrchestrationRun.

Each step should contain at minimum:

Id
OrchestrationRunId
Stage
Status
StartedAtUtc
CompletedAtUtc
Duration information if appropriate
AttemptNumber
FailureType
FailureMessage
Metadata

Do not duplicate data already represented by the parent OrchestrationRun unless necessary.

Use nullable values where a value is not available.

Do not fabricate metadata.

Metadata may use a simple SQLite-compatible representation.

A nullable string containing serialized JSON is acceptable.

Do not introduce unnecessary JSON infrastructure.

==================================================
STAGE MODEL
==================================================

Inspect the real orchestration flow before defining the enum.

The trace must represent actual stages currently executed by the repository.

Likely stages include:

WorkspacePreparation
ContextRetrieval
TaskPlanning
ModelSelection
AgentExecution
Build
Test
Repair
CodeReview
HumanApproval
Evaluation
Learning

Do not force a stage into the trace if that stage does not actually exist as an identifiable operation.

Do not fabricate successful steps.

The implementation must reflect the real code flow.

==================================================
STEP STATUS
==================================================

Support a lifecycle equivalent to:

Pending
Running
Succeeded
Failed
Skipped

Use existing status conventions if equivalent types already exist.

Do not create duplicate enums unnecessarily.

==================================================
ATTEMPTS
==================================================

The system already supports repair/retry behavior.

Execution trace must make retries observable.

For operations that can occur repeatedly, record attempt numbers.

Examples:

Build attempt 1
Test attempt 1
Repair attempt 1

Build attempt 2
Test attempt 2
Repair attempt 2

Do not invent retry behavior.

Only record attempts that actually occur in the existing execution flow.

==================================================
INSTRUMENTATION
==================================================

Instrument the existing code.

Do not rewrite AutonomousCodingOrchestrator.

Do not move orchestration responsibilities.

The trace should be written around existing real execution boundaries.

Inspect and instrument where appropriate:

OrchestrationRunExecutionEngine

AutonomousCodingOrchestrator

ValidationEngine

Build/Test/Repair flow

Code review flow

Approval flow

Evaluation flow

Learning flow

Use a small abstraction if required so Application/orchestration code does not depend directly on EF Core.

Suggested conceptual abstraction:

IOrchestrationRunTraceService

or another name consistent with repository conventions.

Possible operations:

StartStepAsync
CompleteStepAsync
FailStepAsync
SkipStepAsync

Do not force these exact names if existing conventions suggest better naming.

==================================================
FAILURE HANDLING
==================================================

If an orchestration stage throws:

1. mark the active trace step as Failed
2. record failure information
3. preserve the existing exception/orchestration behavior
4. do not swallow exceptions merely for tracing

The trace must never change successful business behavior.

Observability must be non-invasive.

If persistence of the trace itself fails, inspect existing error handling conventions and choose the smallest behavior-preserving solution.

Do not silently hide primary orchestration failures.

==================================================
MODEL AND AGENT METADATA
==================================================

When the existing execution flow already exposes information, record metadata such as:

provider
model
selected model identifier
agent executor type
attempt number

Do not modify interfaces throughout the entire solution merely to collect metadata.

Do not add provider-specific assumptions.

If metadata is unavailable, leave it absent.

==================================================
PERSISTENCE
==================================================

Use the existing EF Core SQLite infrastructure.

Add the necessary:

Domain relationship
DbSet
EF configuration
indexes
migration

Recommended indexes should support queries by:

OrchestrationRunId
Stage
Status
StartedAtUtc

Use the existing migration conventions.

Do not introduce:

PostgreSQL
Redis
Elasticsearch
OpenTelemetry storage
message queues
external tracing systems

The system must remain locally runnable using the current SQLite setup.

==================================================
QUERY CAPABILITIES
==================================================

Extend the existing orchestration run query capabilities.

Do not create an unrelated execution API.

Support:

GET /api/orchestration-runs/{id}

The run detail must include:

run metadata
overall status
timestamps
failure information
ordered execution trace

If a detail endpoint already exists, extend it rather than duplicating it.

Also support an appropriate project-scoped query if the current API architecture already supports orchestration run listing.

Do not expose EF entities directly.

==================================================
ORDERING
==================================================

Trace steps must be returned deterministically.

The response must reflect actual execution order.

If timestamps alone can be ambiguous, introduce a minimal sequence/order field.

Do not rely on database insertion order.

==================================================
API RESPONSE
==================================================

A run detail response should conceptually contain:

{
  "id": "...",
  "projectId": "...",
  "status": "Running",
  "startedAtUtc": "...",
  "completedAtUtc": null,

  "trace": [
    {
      "stage": "TaskPlanning",
      "status": "Succeeded",
      "attemptNumber": 1,
      "startedAtUtc": "...",
      "completedAtUtc": "...",
      "failureMessage": null,
      "metadata": {
        "model": "...",
        "provider": "..."
      }
    }
  ]
}

Do not force this exact JSON structure if existing API DTO conventions differ.

==================================================
TESTING
==================================================

Add focused automated tests.

At minimum test:

1. trace step creation
2. successful step completion
3. failed step completion
4. skipped step
5. deterministic ordering
6. multiple attempts
7. trace attached to the correct OrchestrationRun
8. orchestration integration for a successful stage
9. orchestration integration for a failed stage
10. failure of a stage does not swallow the original orchestration failure
11. API/query response includes ordered trace data where endpoint testing conventions already exist

Follow the repository's existing test conventions.

Do not create an enormous test suite.

Do not introduce a new test framework.

==================================================
OUT OF SCOPE
==================================================

Do not implement:

- Grafana
- Prometheus
- Jaeger
- distributed tracing
- OpenTelemetry exporters
- SignalR dashboards
- Kubernetes
- concurrency changes
- Worker MaxConcurrentRuns
- queues
- external observability infrastructure
- OpenViking activation
- fake OpenViking endpoints

OpenViking must remain consistent with the current intentionally deferred state.

Do not change:

OpenViking:Enabled=false

unless the repository already contains a real configured endpoint and this phase explicitly requires it, which it does not.

==================================================
IMPORTANT EXISTING DESIGN BOUNDARIES
==================================================

Preserve these existing intentional decisions:

1. OpenViking is disabled by default and LocalContextProvider remains the active provider.

2. TestFailures context currently has no fabricated data source.

3. Worker intentionally does not register API-only IOrchestrationService.

4. Worker concurrency behavior is not part of this phase.

5. Phase 17.1 already validated the DI graph; do not redesign registrations.

==================================================
IMPLEMENTATION PROCESS
==================================================

Before editing:

1. Inspect the current OrchestrationRun Domain model.
2. Inspect its repository and EF configuration.
3. Inspect OrchestrationRunExecutionEngine.
4. Inspect AutonomousCodingOrchestrator.
5. Inspect build/test/repair flow.
6. Inspect existing orchestration API endpoints.
7. Inspect current tests.
8. Inspect exception handling introduced in production hardening.

Then implement the smallest coherent extension.

==================================================
VALIDATION
==================================================

When finished run:

dotnet build apps\AI.Factory.slnx

dotnet test apps\AI.Factory.slnx

Fix all failures introduced by this phase.

==================================================
FINAL REPORT
==================================================

Provide a concise report containing:

1. Existing architecture inspected
2. Domain changes
3. Application changes
4. Infrastructure changes
5. API changes
6. Orchestration instrumentation points
7. Tests added
8. Migration created
9. Build result
10. Test result
11. Intentionally deferred items

Explicitly identify anything you deliberately did not implement.

Do not leave placeholder implementations.

Do not fabricate execution events.

The persistent trace must represent real events produced by the existing orchestration flow.