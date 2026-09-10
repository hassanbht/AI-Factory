# Phase 18.1.5 — Agent Orchestration Service + Worker

## Phase Objective

Implement the first real **Factory-owned Agent Orchestration layer** and connect it to the existing Worker execution pipeline.

The goal of this phase is to make a persisted `AgentRun` executable through the following architecture:

```text
AgentRun
   │
   ▼
AgentOrchestrator
   │
   ├── AgentDefinition
   ├── IAgentToolRegistry
   ├── IAgentMemory
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
HermesAgentExecutor
   │
   ▼
Hermes process/container
```

The Factory must remain the owner of durable workflow/execution state.

Hermes remains the agent runtime.

Do not move workflow state ownership into Hermes.

---

# 1. Repository Inspection

Before changing anything:

1. Read `AGENTS.md`.
2. Inspect the current repository structure.
3. Inspect the complete implementations/contracts of:
   - `AgentRun`
   - `AgentDefinition`
   - `AgentRunStatus`
   - `IAgentRuntime`
   - `HermesAgentRuntime`
   - `IAgentTool`
   - `IAgentToolRegistry`
   - `IAgentMemory`
   - `LocalAgentMemoryProvider`
   - `AgentRunRepository`
   - `FactoryDbContext`
   - existing Worker services
   - existing background execution abstractions
   - existing resilience/decorator infrastructure
4. Inspect current DI registration in:
   - `Api/Program.cs`
   - `Worker/Program.cs`
5. Inspect existing tests around:
   - `AgentRun`
   - runtime execution
   - Worker lifecycle
   - repositories
   - resilience
6. Search for any existing orchestration/background-job abstraction before creating a new one.

Do not create duplicate abstractions if an equivalent already exists.

---

# 2. Phase 18.1.4 Baseline

Treat Phase 18.1.4 as complete and validated.

Do NOT redesign or recreate:

- `IAgentTool`
- `IAgentToolRegistry`
- `AgentToolRegistry`
- `IAgentMemory`
- `LocalAgentMemoryProvider`
- `ReadArtifactTool`
- `AgentMemoryRecord`
- the `agent_memory` migration

Reuse them as they currently exist.

The previous phase already validated:

- 764/764 tests passing
- Debug and Release builds clean
- migration application against scratch SQLite
- tool registry behavior
- memory storage/search/scope isolation
- secure artifact reading

Do not repeat the entire Phase 18.1.4 test matrix individually unless a change requires it.

---

# 3. Core Architectural Requirement

Create a Factory-owned orchestration abstraction, preferably:

```csharp
IAgentOrchestrator
```

with an implementation such as:

```csharp
AgentOrchestrator
```

unless repository conventions indicate a better existing name.

The orchestrator must be responsible for coordinating one `AgentRun`.

It must NOT become another agent runtime.

Responsibilities belong approximately here:

```text
AgentOrchestrator
    ├── load AgentRun
    ├── validate execution eligibility
    ├── load AgentDefinition
    ├── establish execution context
    ├── resolve tools
    ├── resolve memory
    ├── invoke IAgentRuntime
    ├── persist execution result
    ├── persist status transitions
    ├── persist timing/error information
    └── return deterministic orchestration result
```

The orchestrator must not directly:

- spawn processes
- invoke Docker
- invoke Hermes CLI
- manipulate Hermes SQLite state
- implement retry policies already owned by the runtime/executor layer
- contain model-provider routing logic
- implement tool-specific behavior
- implement memory-provider-specific behavior

---

# 4. AgentRun Lifecycle

Use the existing canonical `AgentRunStatus`.

Do not introduce another status enum.

The orchestrator must enforce valid lifecycle transitions through the existing domain transition mechanism.

At minimum, the execution flow should support:

```text
Created
   ↓
Queued
   ↓
Running
   ↓
Succeeded
```

and failure paths such as:

```text
Running → Failed
Running → TimedOut
Running → Cancelled
```

If the current domain transition rules use slightly different valid transitions, preserve those rules instead of inventing new ones.

The orchestrator must never bypass centralized status-transition validation.

---

# 5. Execution Context

Introduce the minimum execution-context object required to pass Factory information to the runtime.

The exact shape must follow existing conventions.

It should contain, where appropriate:

- `AgentRunId`
- `AgentDefinitionId`
- `ProjectId`
- task/user prompt or execution input
- workspace information if already represented by existing contracts
- available tool names
- relevant memory/context information
- cancellation token
- correlation/execution identifier

Do not expose database entities directly to the runtime if a dedicated context contract is cleaner.

Do not put provider-specific Hermes configuration into the domain model.

---

# 6. Tool Registry Integration

The orchestrator must resolve available tools through:

```csharp
IAgentToolRegistry
```

Do not instantiate tools manually.

The orchestrator should be able to determine the tool set available to an execution.

The resulting tool names must be deterministic.

The execution result must populate the existing/potential `ToolCallNames` field on `AgentRun` if that field already exists.

Do not create a second tool-call history model in this phase unless the existing domain model makes it necessary.

For the first implementation:

- `ReadArtifactTool` is sufficient.
- Do not add shell execution.
- Do not add arbitrary filesystem write.
- Do not add browser automation.
- Do not add Git operations.
- Do not add unrestricted command execution.

---

# 7. Memory Integration

The orchestrator must resolve contextual memory through:

```csharp
IAgentMemory
```

Use the existing `LocalAgentMemoryProvider`.

The orchestrator must not depend directly on:

```text
SQLite
FactoryDbContext
AgentMemoryRecord
OpenViking
```

Memory access must remain behind `IAgentMemory`.

For this phase, a minimal flow is sufficient:

```text
AgentRun
   ↓
construct memory query
   ↓
IAgentMemory.SearchAsync(...)
   ↓
relevant context
   ↓
runtime execution
```

Do not implement OpenViking integration yet.

Do not introduce a second memory database.

Do not turn memory into workflow state.

---

# 8. Runtime Integration

Use the existing:

```csharp
IAgentRuntime
```

implementation:

```text
HermesAgentRuntime
```

Do not modify the runtime contract unless absolutely required by an actual architectural incompatibility.

The orchestrator must call the runtime through the interface.

Never call `HermesAgentExecutor` directly from the orchestrator.

Never call `SystemProcessRunner` directly from the orchestrator.

The desired dependency direction is:

```text
Orchestration
    ↓
IAgentRuntime
    ↓
HermesAgentRuntime
    ↓
IAgentExecutor
```

---

# 9. Result Handling

Define or reuse an orchestration result contract.

It should represent at minimum:

- success/failure
- runtime result
- failure category if available
- error information
- execution duration
- tool calls
- useful output/result text where already supported
- correlation information

Avoid leaking low-level process details into the domain.

Do not store secrets, API keys, authorization headers, or raw credentials in `AgentRun`.

---

# 10. Idempotency

The orchestrator must handle repeated execution attempts safely.

Before starting execution, determine the current state of the `AgentRun`.

Examples:

- `Succeeded` → do not execute again.
- `Running` → do not start a second concurrent execution unless the existing architecture explicitly supports leases.
- `Cancelled` → do not silently restart.
- `TimedOut` → follow explicit retry semantics only if already supported.
- `Created` / `Queued` → eligible for execution according to domain rules.

Do not invent a distributed locking system in this phase.

If concurrency protection is needed, use the smallest mechanism consistent with the existing repository/Worker architecture.

---

# 11. Worker Integration

Connect the orchestrator to the existing Worker.

Create the smallest appropriate background execution mechanism, for example:

```text
AgentRunWorker
```

or an existing repository-compatible worker abstraction.

The Worker should:

1. discover eligible queued `AgentRun` records;
2. claim/select work safely according to existing infrastructure;
3. invoke `IAgentOrchestrator`;
4. observe cancellation/shutdown;
5. avoid duplicate execution;
6. continue processing after an individual run fails;
7. log structured lifecycle events.

Do not redesign the entire Worker architecture.

Do not build a general-purpose job queue.

Do not implement distributed scheduling.

The Worker must remain a thin execution host.

---

# 12. Worker Shutdown

Graceful shutdown is mandatory.

When the application receives cancellation:

- stop accepting new AgentRuns;
- allow current execution to observe cancellation;
- do not leave unhandled background exceptions;
- do not mark a successful execution as failed merely because the Worker is shutting down;
- persist a meaningful final state where the current domain model permits it.

Follow existing Worker lifecycle conventions.

---

# 13. Concurrency

Implement conservative concurrency.

For the first version:

```text
one Worker execution loop
one AgentRun at a time
```

is acceptable and preferred unless the existing Worker architecture already provides safe bounded concurrency.

Do not optimize for throughput yet.

Correctness is more important than parallelism.

The design should not prevent future bounded concurrency.

---

# 14. Failure Isolation

A single failed AgentRun must not terminate the Worker process.

The Worker loop should conceptually behave like:

```text
while not cancelled:
    run = get_next_eligible_run()

    if no run:
        wait/poll

    try:
        orchestrator.ExecuteAsync(run)
    catch:
        log
        continue
```

However, use existing resilience/error-handling conventions rather than copying this exact pseudocode.

Never swallow errors silently.

---

# 15. Persistence

Persist the AgentRun lifecycle changes using the existing repository/domain infrastructure.

At minimum:

```text
Queued
Running
Succeeded / Failed / TimedOut / Cancelled
```

Persist appropriate timestamps/results/errors if the existing model supports them.

If the model lacks required fields, add only the minimum required fields.

Do not redesign `AgentRun`.

Do not modify `HermesExecution` semantics.

Remember:

```text
AgentRun       = Factory durable execution state
HermesExecution = legacy/runtime execution record
```

They remain separate.

---

# 16. Database Changes

Before creating a migration:

1. Determine whether the current schema can support the orchestration lifecycle.
2. Prefer existing columns and repositories.
3. Add only genuinely required fields.

If no schema change is required:

```text
NO MIGRATION
```

If a schema change is required:

- create one additive migration;
- never rewrite previous migrations;
- verify clean database migration;
- verify existing migrations remain unchanged.

---

# 17. Observability

Add structured logging for:

```text
AgentRun queued
AgentRun started
AgentRun completed
AgentRun failed
AgentRun timed out
AgentRun cancelled
Worker started
Worker stopped
Worker idle
```

Include where available:

- AgentRunId
- AgentDefinitionId
- ProjectId
- correlation/execution id
- duration
- failure category

Never log:

- API keys
- tokens
- passwords
- authorization headers
- secret environment variables
- full sensitive tool input

Do not build the complete metrics/dashboard layer yet.

That belongs to Phase 18.1.6.

---

# 18. DI

Wire the orchestration service into both relevant application hosts according to existing conventions.

Expected dependency graph:

```text
IAgentOrchestrator
    → AgentOrchestrator

IAgentRuntime
    → HermesAgentRuntime

IAgentToolRegistry
    → AgentToolRegistry

IAgentMemory
    → LocalAgentMemoryProvider
```

Worker-specific orchestration components should only be registered where appropriate.

Avoid duplicated composition logic if the repository already has shared registration infrastructure.

---

# 19. Deterministic Test Doubles

Create or reuse deterministic test doubles where necessary:

```text
FakeAgentRuntime
FakeAgentToolRegistry
FakeAgentMemory
```

Do not introduce Moq if the project convention is hand-written fakes.

Tests must not depend on:

- real Hermes
- Docker
- network
- OpenRouter
- OpenViking
- real credentials

Those belong to the later E2E integration phase.

---

# 20. Required Tests

Add deterministic tests covering at minimum:

### Orchestrator

- valid AgentRun executes;
- AgentDefinition is resolved;
- runtime is invoked through `IAgentRuntime`;
- tools are resolved through `IAgentToolRegistry`;
- memory is resolved through `IAgentMemory`;
- successful execution transitions to `Succeeded`;
- runtime failure transitions to `Failed`;
- timeout transitions correctly;
- cancellation is handled correctly;
- invalid AgentRun state is rejected;
- already-succeeded run is not executed again;
- running run is not executed concurrently;
- tool names are persisted deterministically;
- execution result is persisted.

### Worker

- Worker starts correctly;
- queued AgentRun is discovered;
- AgentRun is passed to orchestrator;
- one failed AgentRun does not kill the Worker;
- cancellation stops the Worker cleanly;
- idle Worker does not spin aggressively;
- shutdown does not create unobserved exceptions.

### Integration-level deterministic test

Using SQLite/in-memory infrastructure already used by the project:

```text
Queued AgentRun
      ↓
Worker
      ↓
AgentOrchestrator
      ↓
FakeAgentRuntime
      ↓
AgentRun persisted as Succeeded
```

This test is important.

It proves the complete Factory-owned orchestration path without requiring real Hermes.

---

# 21. Do NOT Test Real Hermes Yet

Do not attempt to prove real Hermes integration in this phase unless the existing environment makes it trivial and deterministic.

A real Hermes smoke test remains a later E2E concern.

Do not claim:

```text
real Hermes integration verified
```

based only on mocked/fake runtime tests.

The current phase should prove:

```text
Factory orchestration → IAgentRuntime
```

not:

```text
Factory orchestration → real Hermes process
```

---

# 22. No-Goals / Explicitly Do Not Implement

Do NOT implement in Phase 18.1.5:

- REST API for AgentRuns;
- dashboard;
- frontend;
- multi-agent delegation;
- agent teams;
- DAG execution;
- task planning;
- autonomous project generation;
- OpenViking integration;
- OpenRouter routing changes;
- new LLM provider abstraction;
- shell/terminal agent tools;
- browser tools;
- Git tools;
- code-generation tools;
- arbitrary filesystem write tools;
- distributed locks;
- distributed queues;
- horizontal Worker scaling;
- advanced scheduling;
- full metrics system;
- production deployment redesign;
- CI/CD redesign.

Those belong to later phases.

---

# 23. Security Requirements

The orchestrator must treat all AgentRun input as untrusted.

Do not allow:

- arbitrary host filesystem access;
- arbitrary process execution;
- secret leakage through logs;
- credentials embedded in AgentRun persistence;
- bypassing existing workspace security;
- tool invocation outside the registry.

Never allow the AgentRun itself to select an arbitrary .NET type or implementation.

Tool resolution must remain registry-controlled.

---

# 24. Documentation

Create:

```text
.ai/context/phase-18.1.5-agent-orchestration-worker.md
```

Document:

1. architecture;
2. execution flow;
3. AgentRun lifecycle;
4. Worker behavior;
5. dependency graph;
6. idempotency rules;
7. failure handling;
8. cancellation/shutdown behavior;
9. tool/memory integration;
10. tests and empirical validation;
11. known limitations;
12. next phase.

Do not claim real Hermes E2E validation.

---

# 25. Validation Gates

Run targeted tests first.

Then:

```text
dotnet build apps/AI.Factory.slnx
```

Debug and Release must compile with:

```text
0 warnings
0 errors
```

Run the new/affected tests.

Then run:

```text
dotnet test apps/AI.Factory.slnx
```

Run the full regression suite once.

Do not repeatedly run the entire 700+ test suite unless investigating a failure.

If a database schema changed, also perform a clean SQLite migration validation.

---

# 26. Empirical Evidence Rules

The final report must contain only commands/results actually executed.

Never write:

```text
PASS
```

for a test that was not actually run.

Never claim:

```text
Hermes integration verified
```

unless a real Hermes execution was actually performed.

If something could not be tested, explicitly state:

```text
NOT VERIFIED
```

and explain why.

---

# 27. Failure / NO-GO Conditions

Return **NO-GO** if any of these occur:

- `AgentRun` lifecycle can be bypassed;
- duplicate AgentRun execution is possible under the implemented Worker behavior;
- Worker crashes because of one AgentRun failure;
- orchestration directly depends on Hermes implementation;
- orchestration directly accesses Docker/process APIs;
- tools bypass `IAgentToolRegistry`;
- memory bypasses `IAgentMemory`;
- secrets are persisted or logged;
- existing migrations are rewritten;
- full regression introduces unexplained failures;
- cancellation causes unobserved exceptions;
- Debug or Release build has warnings/errors.

Do not hide architectural compromises.

---

# 28. Definition of Done

Phase 18.1.5 is **GO** only when all of the following are true:

- `IAgentOrchestrator` exists with a clean Factory-owned contract;
- `AgentOrchestrator` executes an `AgentRun`;
- AgentRun lifecycle is persisted correctly;
- `IAgentRuntime` is the only runtime boundary;
- `IAgentToolRegistry` is actually integrated;
- `IAgentMemory` is actually integrated;
- Worker discovers and executes queued AgentRuns;
- one failed AgentRun cannot terminate the Worker;
- cancellation/shutdown works correctly;
- duplicate execution is prevented according to the implemented concurrency model;
- deterministic orchestration/Worker tests pass;
- full regression passes;
- Debug build passes;
- Release build passes;
- no unjustified migration changes exist;
- phase documentation exists;
- final report contains actual empirical evidence.

---

# 29. Final Report Format

At the end provide:

## Implementation

- exact components added/modified;
- important architectural decisions.

## AgentRun Lifecycle

Show the implemented state flow.

## Worker

Explain polling/claiming/execution/shutdown behavior.

## Tool + Memory Integration

Explain exactly how they enter orchestration.

## Tests

Report:

```text
Targeted tests: X/X
Full regression: X/X
```

Only use actual numbers.

## Build

Report Debug and Release results.

## Database

State whether a migration was required.

## Real Hermes

Explicitly state:

```text
Real Hermes E2E: VERIFIED
```

or:

```text
Real Hermes E2E: NOT VERIFIED
```

Do not infer it.

## Problems

List any bugs found and fixed.

## Final Decision

Exactly one:

```text
GO
```

or

```text
NO-GO
```

## Next Step

If GO, identify:

```text
Phase 18.1.6 — Agent API + Metrics/Observability
```

The next phase should expose the already-working AgentRun/orchestration capabilities through the API and establish operational observability, without redesigning the orchestration core.