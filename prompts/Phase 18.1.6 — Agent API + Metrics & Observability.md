# Phase 18.1.6 — Agent API + Metrics & Observability

## Phase Objective

Build the first production-facing API surface for the canonical `AgentRun` domain and add operational metrics/observability around the already-implemented Agent orchestration pipeline.

Phase 18.1.5 is considered **GO** and must be treated as the implementation baseline.

The current execution architecture is:

```text
API
 │
 ▼
AgentRun / AgentDefinition
 │
 ▼
IAgentOrchestrator
 │
 ▼
IAgentRuntime
 │
 ▼
HermesAgentRuntime
 │
 ▼
IAgentExecutor
 │
 ▼
Hermes
```

and background execution is:

```text
Queued AgentRun
      │
      ▼
AgentRunWorker
      │
      ▼
IAgentOrchestrator
      │
      ▼
IAgentRuntime
```

This phase must expose and observe this existing system without redesigning it.

---

# 1. Repository Inspection

Before modifying anything:

1. Read `AGENTS.md`.
2. Inspect the complete current implementation of:
   - `AgentRun`
   - `AgentDefinition`
   - `AgentRunStatus`
   - `IAgentRunRepository`
   - `AgentRunRepository`
   - `IAgentOrchestrator`
   - `AgentOrchestrator`
   - `AgentRunWorker`
   - `IAgentRuntime`
   - `HermesAgentRuntime`
   - `IAgentToolRegistry`
   - `IAgentMemory`
3. Inspect existing API controllers/endpoints and conventions.
4. Inspect existing DTO/request/response patterns.
5. Inspect existing authentication/authorization infrastructure.
6. Inspect existing operational metrics abstractions:
   - `IOperationalMetrics`
   - health infrastructure
   - worker metrics
   - deployment/operations endpoints
7. Inspect existing logging/correlation conventions.
8. Inspect existing tests for API, operations, metrics, health, and Worker behavior.

Search first.

Do not create duplicate infrastructure where an existing abstraction already satisfies the requirement.

---

# 2. Phase 18.1.5 Baseline

Treat Phase 18.1.5 as complete.

Do NOT redesign:

- `IAgentOrchestrator`
- `AgentOrchestrator`
- `AgentRunWorker`
- `IAgentRuntime`
- `HermesAgentRuntime`
- `IAgentToolRegistry`
- `IAgentMemory`
- `AgentRun` lifecycle
- repository claim mechanism

Do not repeat the full Phase 18.1.5 implementation.

Do not change the AgentRun state machine unless an actual API requirement exposes a concrete domain defect.

The previous phase validated:

```text
Targeted orchestrator/repository tests: 19/19
Worker tests: 5/5
Full regression: 783/783
Debug build: 0 warnings/errors
Release build: 0 warnings/errors
Database migration: not required
Real Hermes E2E: NOT VERIFIED
```

These are the baseline facts.

---

# 3. API Architecture

Create a clean API boundary for Agent operations.

Prefer:

```text
/api/agents/...
```

unless the existing API versioning/routing conventions require another route.

The API must use DTOs.

Do not expose EF entities directly from controllers.

Do not expose:

- database implementation details;
- Hermes internal state;
- Hermes SQLite state;
- internal process IDs;
- API keys;
- provider credentials;
- secret configuration.

---

# 4. AgentDefinition API

Expose the minimum useful operations for AgentDefinition management.

At minimum evaluate whether the existing domain supports:

```text
GET    /api/agents/definitions
GET    /api/agents/definitions/{id}
POST   /api/agents/definitions
```

If update/delete operations are not necessary for the current domain, do not add them merely for completeness.

The API should validate:

- required name;
- valid model/runtime configuration;
- enabled/disabled state;
- project association where applicable.

Use existing domain validation.

Do not duplicate domain rules inside controllers.

---

# 5. AgentRun API

Expose the minimum operations needed to create, inspect, and control an AgentRun.

At minimum:

```text
POST /api/agents/runs
GET  /api/agents/runs/{id}
GET  /api/agents/runs
```

Evaluate whether the current domain safely supports:

```text
POST /api/agents/runs/{id}/cancel
```

If cancellation can be implemented cleanly using the existing AgentRun lifecycle, implement it.

Do not introduce a second cancellation mechanism.

---

# 6. AgentRun Creation

Creating a run must go through the existing domain/repository/application architecture.

The controller must not:

- directly manipulate EF entities;
- directly invoke Hermes;
- directly call `HermesAgentRuntime`;
- directly call `AgentRunWorker`.

The expected flow is:

```text
HTTP POST
   ↓
Request DTO
   ↓
Application service/use-case
   ↓
AgentDefinition validation
   ↓
AgentRun creation
   ↓
Queued state
   ↓
Repository
   ↓
Worker discovers run
```

The API should normally create a run in `Queued` state rather than synchronously executing it.

Do not make HTTP requests wait for Hermes execution.

---

# 7. AgentRun Query API

Implement list/detail endpoints using DTOs.

The list endpoint should support useful filters where the repository/domain can support them without overengineering.

Potential filters:

```text
projectId
agentDefinitionId
status
from
to
page
pageSize
```

Only implement filters that fit existing repository conventions.

Do not introduce a large query framework.

Pagination should have safe limits.

For example:

```text
default page size
maximum page size
```

must prevent accidental unbounded database queries.

---

# 8. API Response Model

AgentRun detail should expose useful Factory-level information such as:

- `id`
- `projectId`
- `agentDefinitionId`
- `status`
- creation time
- queued time if available
- started time if available
- completed time if available
- duration if available
- result/output if already represented
- failure category if available
- error information safe for clients
- tool call names
- correlation/execution identifier

Do not expose raw secrets or low-level Hermes internals.

Do not invent fields merely to make the response look complete.

Use the existing model where possible.

---

# 9. API Error Handling

Use consistent HTTP semantics.

At minimum distinguish:

```text
400 Bad Request
404 Not Found
409 Conflict
500 Internal Server Error
```

where appropriate.

Examples:

- malformed request → `400`
- unknown AgentRun → `404`
- invalid lifecycle operation → `409`
- unexpected server failure → `500`

Do not leak stack traces in production API responses.

Internal details belong in structured logs.

---

# 10. Idempotency / Duplicate Requests

Consider duplicate API requests carefully.

For example:

```text
POST /api/agents/runs
```

must not accidentally execute a run synchronously.

If the existing application infrastructure has an idempotency mechanism, reuse it.

Do not introduce a large idempotency subsystem in this phase.

For cancellation:

```text
cancel already-cancelled
cancel succeeded
cancel failed
```

must produce deterministic behavior according to the existing state machine.

---

# 11. Authorization / Security Boundary

Inspect the current authentication infrastructure first.

If authentication/authorization already exists:

- protect Agent endpoints according to existing conventions;
- apply least privilege;
- do not create a parallel auth system.

If authentication is not yet implemented for the project, do not invent a fake security model solely for this phase.

Document the current security boundary.

Agent endpoints must never expose credentials.

---

# 12. Metrics & Observability Objective

Add operational visibility to the Agent subsystem.

Use existing:

```text
IOperationalMetrics
```

or the repository's existing metrics abstraction if it already supports the required information.

Do not create a parallel metrics framework.

The system should be able to answer questions such as:

```text
How many AgentRuns are queued?
How many are running?
How many succeeded?
How many failed?
How many timed out?
How many were cancelled?
How long do executions take?
How many executions are currently active?
How many Worker claims failed?
```

---

# 13. Agent Metrics

Add the minimum useful metrics.

Prefer counters/gauges/histograms according to the existing metrics implementation.

Conceptually:

```text
agent_runs_created_total
agent_runs_started_total
agent_runs_succeeded_total
agent_runs_failed_total
agent_runs_timed_out_total
agent_runs_cancelled_total

agent_runs_active
agent_runs_queued

agent_run_duration
```

Names must follow the project's existing metric naming conventions.

Do not blindly use these exact names if the existing infrastructure uses another convention.

---

# 14. Worker Metrics

Expose useful Worker-level information.

At minimum evaluate:

```text
agent_worker_started
agent_worker_active
agent_worker_idle
agent_worker_claim_success
agent_worker_claim_conflict
agent_worker_execution_failure
```

Do not duplicate existing generic Worker metrics.

Extend existing metrics if possible.

---

# 15. Metrics Cardinality

Avoid high-cardinality metric labels.

Do NOT use:

```text
AgentRunId
ProjectId
prompt
raw error message
tool input
user input
```

as metric labels unless there is a compelling existing convention.

Good labels may include bounded values such as:

```text
status
failure_category
```

if supported by the existing metrics implementation.

Metrics must remain safe for production-scale usage.

---

# 16. Structured Logging

Ensure the Agent API and orchestration lifecycle produce structured logs.

Important events:

```text
AgentRun created
AgentRun requested
AgentRun queried
AgentRun cancelled
AgentRun execution started
AgentRun execution completed
AgentRun execution failed
```

Where appropriate include:

```text
AgentRunId
AgentDefinitionId
ProjectId
correlation/execution id
duration
status
failure category
```

Never log:

- prompts containing secrets;
- API keys;
- access tokens;
- passwords;
- authorization headers;
- environment secrets;
- raw sensitive tool inputs.

---

# 17. Correlation

Inspect the existing correlation/request-id infrastructure.

Reuse it.

If an AgentRun already has an execution/correlation identifier, expose it consistently through logs and API responses.

The goal is to make this trace possible:

```text
HTTP request
   ↓
AgentRun creation
   ↓
Worker claim
   ↓
Orchestrator
   ↓
Runtime
   ↓
completion/failure
```

Do not introduce distributed tracing infrastructure if the project does not already have it.

Implement only the minimum required correlation behavior.

---

# 18. Health / Readiness

Inspect the existing health endpoints.

Add Agent-specific health information only if it provides meaningful operational value.

Potential checks:

```text
Agent Worker registered
Agent Worker running
Agent subsystem dependencies available
```

Do not make the API unhealthy merely because there are currently no queued AgentRuns.

Do not make real Hermes a mandatory health dependency if real Hermes E2E is not yet proven.

---

# 19. API + Worker Separation

Maintain the architecture:

```text
API
 └── creates/queries/cancels AgentRuns

Worker
 └── executes queued AgentRuns

Orchestrator
 └── coordinates one AgentRun

Runtime
 └── executes through Hermes boundary
```

The API must not execute agents synchronously.

The Worker must not expose HTTP concerns.

The orchestrator must not know about HTTP.

---

# 20. Tests — API

Add deterministic API tests for:

### Definitions

- create valid definition;
- invalid definition;
- get existing definition;
- get missing definition;
- list definitions.

### AgentRuns

- create run;
- created run becomes `Queued`;
- get existing run;
- get missing run;
- list runs;
- status filtering if implemented;
- pagination if implemented;
- cancel eligible run;
- cancel invalid/terminal run;
- correct HTTP status codes;
- sensitive information is not returned.

Do not use real Hermes.

---

# 21. Tests — Metrics

Add deterministic tests proving:

- run creation increments appropriate metric;
- execution start increments appropriate metric;
- success increments success metric;
- failure increments failure metric;
- timeout increments timeout metric;
- cancellation increments cancellation metric;
- active/queued gauges behave correctly;
- duration metric is recorded;
- metric labels remain bounded.

Use the project's existing metrics test infrastructure.

Do not depend on timing-sensitive assertions where avoidable.

---

# 22. Tests — Integration

Add at least one deterministic API/application integration flow:

```text
POST AgentRun
      ↓
201/202 response
      ↓
AgentRun persisted as Queued
      ↓
GET AgentRun
      ↓
status = Queued
```

If the existing test infrastructure allows Worker execution:

```text
POST AgentRun
      ↓
Queued
      ↓
Worker
      ↓
FakeAgentRuntime
      ↓
Succeeded
      ↓
GET AgentRun
      ↓
Succeeded
```

This must remain deterministic and must not require real Hermes.

---

# 23. Existing Regression

Do not repeat the complete existing test suite unnecessarily during implementation.

Use:

1. targeted API tests;
2. targeted metrics tests;
3. affected Worker/orchestration tests if modified;
4. full regression once after implementation stabilizes.

The current baseline is 783/783.

Do not claim a new total until the actual suite is executed.

---

# 24. Database Changes

Prefer no schema changes.

First determine whether current entities already contain everything required by the API.

If DTOs can be built from the existing model:

```text
NO MIGRATION
```

If a schema change is genuinely required:

- add the minimum fields;
- create an additive migration;
- do not modify previous migrations;
- validate clean database migration.

Do not introduce API-specific persistence tables unless there is a real requirement.

---

# 25. No-Goals

Do NOT implement:

- real Hermes E2E;
- OpenViking integration;
- multi-agent delegation;
- agent teams;
- DAG execution;
- task planning;
- autonomous software generation;
- shell/terminal tools;
- browser tools;
- Git tools;
- code-generation tools;
- dashboard/frontend;
- distributed queue;
- distributed Worker scaling;
- advanced scheduling;
- new LLM provider routing;
- major resilience redesign;
- new authentication framework;
- Kubernetes deployment;
- CI/CD redesign.

These are outside this phase.

---

# 26. Important Architectural Constraint

Keep the separation:

```text
Factory Database
    = durable workflow/execution truth

AgentRun
    = Factory-owned execution state

AgentOrchestrator
    = Factory-owned coordination

IAgentRuntime
    = runtime boundary

HermesAgentRuntime
    = Hermes adapter

Hermes
    = agent execution runtime

IAgentToolRegistry
    = available tools

IAgentMemory
    = contextual knowledge
```

Do not collapse these responsibilities.

---

# 27. Real Hermes Status

Real Hermes integration remains:

```text
NOT VERIFIED
```

unless this phase happens to execute a genuine Hermes environment.

Do not add an API health check that claims Hermes is healthy merely because `HermesAgentRuntime` is registered.

Real Hermes validation belongs to the E2E phase.

---

# 28. Documentation

Create:

```text
.ai/context/phase-18.1.6-agent-api-metrics.md
```

Document:

1. API architecture;
2. endpoint list;
3. DTOs;
4. AgentRun creation flow;
5. cancellation semantics;
6. query/filter behavior;
7. error semantics;
8. authorization boundary;
9. metrics;
10. logging/correlation;
11. health/readiness behavior;
12. tests;
13. migration status;
14. known limitations;
15. real Hermes verification status;
16. next phase.

---

# 29. Validation Gates

Run targeted tests first.

Then:

```text
dotnet build apps/AI.Factory.slnx
```

Requirements:

```text
Debug:   0 warnings, 0 errors
Release: 0 warnings, 0 errors
```

Run targeted API/metrics tests.

Then run:

```text
dotnet test apps/AI.Factory.slnx
```

Full regression must pass.

If schema changed:

```text
dotnet ef database update
```

against a clean scratch SQLite database and verify all migrations.

Also verify that no previous migration was modified.

---

# 30. Empirical Evidence Rules

The final report must contain only commands actually executed.

Never fabricate:

- endpoint test results;
- metric values;
- Worker behavior;
- Hermes execution;
- health status;
- migration results.

If something was not tested, explicitly report:

```text
NOT VERIFIED
```

---

# 31. Failure / NO-GO Conditions

Return **NO-GO** if:

- API directly invokes Hermes;
- API synchronously waits for AgentRun execution;
- API bypasses `IAgentOrchestrator`;
- Worker architecture is duplicated/reimplemented;
- AgentRun state transitions bypass domain validation;
- sensitive information is exposed;
- high-cardinality metrics are introduced;
- previous migrations are rewritten;
- existing 783-test baseline regresses without explanation;
- Debug or Release build contains warnings/errors;
- API cancellation can create an invalid AgentRun state;
- metrics implementation introduces a second operational metrics system.

---

# 32. Definition of Done

Phase 18.1.6 is **GO** only when:

- AgentDefinition API exists at the appropriate boundary;
- AgentRun creation API exists;
- AgentRun query/detail API exists;
- cancellation is implemented if safely supported by the existing state machine;
- API returns DTOs rather than EF entities;
- API does not execute Hermes synchronously;
- API uses the existing application/orchestration architecture;
- Agent lifecycle is observable;
- Worker/Agent metrics are available;
- structured logs contain useful AgentRun correlation information;
- metrics have bounded cardinality;
- API tests pass;
- metrics tests pass;
- integration tests pass;
- Debug build passes;
- Release build passes;
- full regression passes;
- database status is verified;
- documentation exists;
- real Hermes status is explicitly reported.

---

# 33. Final Report

Provide:

## Implementation

Exact files/components added or modified.

## API

List the actual endpoints implemented.

For each endpoint report:

```text
Method
Route
Purpose
Response
```

## AgentRun Lifecycle

Show the actual API → Worker → Orchestrator flow.

## Metrics

List actual metrics added/reused.

## Observability

Report logging/correlation/health changes.

## Tests

Report actual numbers:

```text
API targeted: X/X
Metrics targeted: X/X
Integration: X/X
Full regression: X/X
```

## Build

```text
Debug:   ...
Release: ...
```

## Database

```text
Migration required: YES/NO
```

If YES, report clean migration validation.

## Real Hermes

Exactly one:

```text
Real Hermes E2E: VERIFIED
```

or:

```text
Real Hermes E2E: NOT VERIFIED
```

Do not infer.

## Problems

List actual bugs discovered and fixed.

## Final Decision

Exactly one:

```text
GO
```

or:

```text
NO-GO
```

## Next Step

If GO:

```text
Phase 18.1.7 — End-to-End Agent Execution + Real Hermes Smoke + Stability Gate
```

The next phase should prove the complete production path with a real Hermes environment, validate the Worker → Orchestrator → Runtime → Hermes boundary, and establish the final stability gate before expanding into more advanced agent capabilities.