# Phase 18.1.3 — Hermes IAgentRuntime Implementation

## Phase Objective

Phase 18.1.2 is COMPLETE with a GO decision.

The canonical Agent domain model now exists:

```text
AgentDefinition
       ↓
    AgentRun
```

The legacy WorkItem-bound Hermes process record has been separated as:

```text
HermesExecution
```

The new `AgentRun` is the canonical, runtime-agnostic execution record.

The objective of this phase is to implement the first real runtime behind:

```text
IAgentRuntime
```

using Hermes Agent.

The resulting architecture must be:

```text
AgentDefinition
       ↓
    AgentRun
       ↓
 IAgentRuntime
       ↓
 HermesAgentRuntime
       ↓
 Hermes Agent
       ↓
 Model Provider / Tools / Skills
```

The Factory owns durable execution state.

Hermes owns agent execution.

Do not reverse these responsibilities.

---

# 1. Repository Inspection

Before changing anything:

1. Read `AGENTS.md`.
2. Inspect the current repository structure.
3. Read the Phase 18.1.2 documentation.
4. Inspect:
   - `AgentDefinition`
   - `AgentRun`
   - `AgentRunStatus`
   - `AgentRunTransitions`
   - `IAgentRuntime`
   - `IAgentTool`
   - `IAgentMemory`
   - `HermesExecution`
   - existing Hermes execution code
   - existing provider/model abstractions from Phase 18.1.1
   - current configuration system
   - current Worker architecture
   - existing process execution utilities
   - existing logging/metrics abstractions
   - existing tests
5. Identify exactly which existing Hermes code can be reused.

Do not rebuild functionality that already exists.

---

# 2. Critical Architectural Rule

Do NOT merge `AgentRun` and `HermesExecution`.

The responsibilities are:

### AgentRun

Durable Factory-level execution lifecycle:

```text
Created
Queued
Running
Waiting
Succeeded
Failed
TimedOut
Cancelled
```

### HermesExecution

Legacy/process-level Hermes execution information retained for compatibility and migration.

### HermesAgentRuntime

The infrastructure implementation of:

```text
IAgentRuntime
```

It executes an AgentRun but does not own the AgentRun lifecycle.

The runtime must return execution results.

The application/orchestration layer will later decide how those results advance AgentRun state.

---

# 3. Implement HermesAgentRuntime

Create an infrastructure implementation equivalent to:

```text
HermesAgentRuntime : IAgentRuntime
```

Place it in the existing Infrastructure structure according to repository conventions.

Do not introduce a second application-level Hermes abstraction if `IAgentRuntime` already provides the required boundary.

---

# 4. Runtime Request Contract

Review the existing `IAgentRuntime` contract.

If it is insufficient, minimally evolve it to support a real Hermes execution.

The runtime request should be capable of carrying:

- `AgentRunId`
- `AgentDefinitionId`
- correlation ID
- agent/system instructions
- user/task prompt
- selected model/provider information
- working directory
- execution timeout
- cancellation token
- optional environment/configuration
- tool availability
- memory/context information where already represented by the contract

Do not put Hermes-specific CLI arguments into the domain model.

Hermes-specific information belongs in Infrastructure.

---

# 5. Runtime Result Contract

The runtime must return a structured result.

At minimum:

```text
Success
Output
Error
Provider
Model
Duration
TokenUsage
Iterations
Artifacts
```

Use the existing contracts from Phase 18.1.2 wherever possible.

Do not duplicate result models unnecessarily.

Large outputs/artifacts must not be blindly stored inside the AgentRun database row.

Use the repository's existing artifact/reference mechanism where available.

---

# 6. Hermes Invocation

Determine how Hermes is actually available in the current deployment.

Do NOT assume:

- a specific host path
- a specific Docker container name
- a specific executable location
- a specific model
- a specific provider
- API keys in source code

Make Hermes execution configurable.

The implementation should support configuration such as:

```text
Hermes:
  ExecutablePath
  Arguments
  WorkingDirectory
  Timeout
```

Use the existing project's configuration conventions rather than introducing an unrelated configuration mechanism.

---

# 7. Process Execution Boundary

If Hermes is executed as a process, isolate process handling behind a small infrastructure abstraction.

For example:

```text
IHermesProcessRunner
```

or an equivalent existing abstraction.

Responsibilities:

- start Hermes
- provide input
- capture stdout
- capture stderr
- return exit code
- support cancellation
- enforce timeout
- avoid deadlocks
- dispose processes correctly

`HermesAgentRuntime` should orchestrate the Hermes-specific semantics.

It should NOT contain low-level process-management complexity if a reusable process abstraction is appropriate.

---

# 8. Cancellation

Cancellation is mandatory.

When the supplied `CancellationToken` is cancelled:

1. request graceful termination if supported;
2. wait only for a bounded period;
3. force terminate if necessary;
4. dispose all process resources;
5. return a deterministic cancelled/failed runtime result according to the established contract.

Never leave orphaned Hermes processes.

Add a test specifically proving cancellation cleanup.

---

# 9. Timeout

Timeout must be enforced independently of the Hermes process itself.

The runtime must distinguish:

```text
Cancelled
```

from:

```text
TimedOut
```

Do not report a timeout as a generic failure.

Timeout must:

1. terminate the Hermes execution;
2. clean up the process;
3. capture useful diagnostics;
4. return a deterministic timeout result.

---

# 10. Exit Code Handling

Define deterministic semantics for Hermes exit codes.

At minimum:

```text
exit code 0
    → successful runtime execution

non-zero
    → failed runtime execution
```

Unless Hermes has already-established repository-specific semantics that require different handling.

Capture stderr for failures.

Do not expose secrets contained in environment variables or command arguments through logs.

---

# 11. Output Handling

Hermes stdout/stderr can be large.

Implement bounded or safe output handling.

Requirements:

- no deadlock caused by stdout/stderr buffering;
- no unbounded memory growth for accidental massive output;
- preserve enough output for diagnostics;
- do not log entire prompts/responses by default;
- never log API keys, tokens, Authorization headers, or `.env` contents.

If truncation is necessary, record that truncation occurred.

---

# 12. Model Routing Integration

Reuse Phase 18.1.1 model/provider abstractions.

Do NOT hardcode:

```text
OpenRouter
```

or a specific model inside `HermesAgentRuntime`.

The flow should be conceptually:

```text
AgentRun
   ↓
Model Router
   ↓
ModelRoute
   ↓
Hermes Runtime
```

The runtime receives the resolved route/configuration.

Provider selection belongs to the existing routing layer.

---

# 13. AgentDefinition Integration

The runtime must execute an AgentDefinition rather than arbitrary hardcoded agent behavior.

At minimum the execution context should be able to resolve:

```text
AgentDefinition.Name
AgentDefinition.Description
AgentDefinition.AgentType
AgentDefinition.SystemPrompt
AgentDefinition.Enabled
```

If an AgentDefinition is disabled, do not execute it.

Do not duplicate AgentDefinition configuration into Hermes-specific classes unless necessary.

---

# 14. Skills and Tools

This phase does NOT implement the complete `IAgentTool` system.

However, establish the runtime boundary so Hermes can later receive tools/skills.

The runtime should not hardcode a list of tools.

Prepare an extension point equivalent to:

```text
IReadOnlyCollection<AgentToolDescriptor>
```

or reuse the existing contract.

For this phase:

- zero tools is valid;
- existing Hermes built-in capabilities may remain available according to configured Hermes behavior;
- Factory-owned tools must NOT yet be implemented.

Do not introduce OpenViking or production memory integration here.

---

# 15. Hermes Session Handling

Investigate how the currently installed Hermes version handles sessions.

Do not assume that every AgentRun must map directly to a persistent Hermes session.

For this phase establish the smallest correct mapping.

The preferred conceptual ownership is:

```text
Factory AgentRun
    ↓
Hermes execution/session
```

The Factory must retain its own durable identity:

```text
AgentRun.Id
CorrelationId
```

Do not make the Hermes session ID the primary identity of an AgentRun.

If Hermes session identifiers are available, store them as optional runtime metadata.

---

# 16. Security

The runtime must not:

- embed API keys;
- persist secrets in AgentRun;
- print environment secrets;
- expose provider credentials in logs;
- construct shell commands through unsafe string concatenation where arguments can be passed safely;
- trust arbitrary user input as executable command-line flags.

Use `ProcessStartInfo.ArgumentList` or the repository's equivalent safe argument mechanism where applicable.

Working directories must be validated according to the application's existing sandbox/security conventions.

---

# 17. Configuration

Add only the configuration required for Hermes runtime execution.

Configuration must be:

- strongly typed where appropriate;
- validated at startup or runtime;
- environment-variable friendly;
- secret-free by default;
- documented.

Do not add a hardcoded production Hermes path.

The runtime should fail clearly when Hermes is unavailable.

Example diagnostic:

```text
Hermes runtime is not configured or executable is unavailable.
```

Avoid vague errors such as:

```text
Execution failed.
```

---

# 18. Dependency Injection

Register:

```text
IAgentRuntime
    → HermesAgentRuntime
```

in Infrastructure composition.

Do not register Hermes-specific implementation directly in Domain.

If the application currently uses conditional/fake implementations in tests, preserve that pattern.

---

# 19. Fake Runtime for Deterministic Tests

Create a deterministic:

```text
FakeAgentRuntime
```

for tests if one does not already exist.

It must allow tests to deterministically produce:

```text
Success
Failure
Timeout
Cancellation
```

This fake runtime is critical because the CI environment must NOT require:

- Hermes installation
- OpenRouter credentials
- Nous credentials
- external model access
- network access

---

# 20. Runtime Unit Tests

Add focused deterministic tests for:

### Success

- Hermes exit code 0
- output captured
- provider/model metadata preserved
- duration recorded

### Failure

- non-zero exit code
- stderr captured
- deterministic error returned

### Timeout

- timeout enforced
- process terminated
- no orphan process
- result classified as TimedOut

### Cancellation

- cancellation propagated
- process terminated
- no orphan process
- result classified as Cancelled

### Output

- stdout/stderr handled safely
- no deadlock
- truncation behavior verified if implemented

### Security

Verify logs/results do not expose configured secret values.

### Configuration

Verify missing/invalid Hermes configuration produces a clear deterministic error.

---

# 21. Real Hermes Smoke Test

Add a real Hermes integration test only if the repository already has a safe mechanism for environment-gated integration tests.

The test must be skipped automatically when Hermes is unavailable.

It must NOT make normal CI depend on:

- a local Hermes installation;
- API keys;
- OpenRouter;
- Nous Portal;
- network connectivity.

If executed locally, it should prove:

```text
AI Factory
    ↓
IAgentRuntime
    ↓
HermesAgentRuntime
    ↓
Hermes
    ↓
successful minimal response
```

Do not claim this test passed unless it was actually executed.

---

# 22. Do NOT Implement Yet

Explicitly out of scope:

- `/api/agents/*`
- Agent orchestration service
- Worker Agent scheduler
- autonomous retries
- DAG scheduling
- multi-agent delegation
- OpenViking integration
- production `IAgentMemory`
- production `IAgentTool`
- dashboard
- consensus workflow
- coding workflow
- project-level autonomous execution
- new CI/CD architecture
- changing GHCR/release infrastructure

Those belong to later phases.

---

# 23. Compatibility With Existing HermesExecution

Existing legacy Hermes process execution must remain functional.

Do not delete `HermesExecution`.

Do not migrate its semantics into the new AgentRun.

If the new runtime needs to create/update HermesExecution records for compatibility, do so explicitly and document why.

Prefer avoiding unnecessary coupling between the new runtime and the legacy record.

---

# 24. Observability

Add structured logs around runtime boundaries:

```text
AgentRuntimeStarted
AgentRuntimeCompleted
AgentRuntimeFailed
AgentRuntimeTimedOut
AgentRuntimeCancelled
```

Logs should contain safe identifiers:

- AgentRunId
- AgentDefinitionId
- CorrelationId
- provider
- model
- duration
- exit status

Do NOT log:

- API keys
- authorization headers
- full environment
- full prompts by default
- full model responses by default

If metrics infrastructure already exists, expose the smallest useful runtime metrics.

Do not redesign the metrics subsystem in this phase.

---

# 25. Error Model

Do not throw arbitrary `Exception` values for every runtime failure.

Introduce or reuse typed runtime failures where appropriate:

```text
HermesUnavailable
HermesStartFailed
HermesTimeout
HermesCancelled
HermesExecutionFailed
HermesOutputInvalid
```

Map infrastructure failures into the existing application/domain error model at the appropriate boundary.

The runtime must preserve enough diagnostic information for later orchestration/retry logic.

---

# 26. Architecture Validation

Verify the final dependency direction:

```text
Domain
   ↑
Application
   ↑
Infrastructure
```

Specifically:

- Domain does not reference Hermes.
- Domain does not reference OpenRouter.
- Domain does not reference OpenViking.
- Application depends only on `IAgentRuntime`.
- Infrastructure implements `IAgentRuntime`.
- Hermes-specific code exists only in Infrastructure.
- Provider/model routing remains behind existing abstractions.

---

# 27. Migration

Do NOT create a new database migration unless this phase actually adds persistent runtime data.

If Hermes runtime metadata requires new persistence fields, justify them first.

Do not modify the Phase 18.1.2 migration.

Do not rewrite migration history.

---

# 28. Validation Gates

Run:

1. Build.
2. New Hermes runtime unit tests.
3. Existing relevant Application/Infrastructure tests.
4. Architecture/dependency verification.
5. Release build.

Run the existing full regression suite only once as the final regression gate.

Do NOT repeat the three-run 18.1.2 stability test unless a regression requires it.

If a real Hermes smoke test is executed, report it separately from deterministic CI tests.

---

# 29. Failure / NO-GO Conditions

Return **NO-GO** if:

- `AgentRun` becomes coupled to Hermes implementation details;
- Hermes replaces `AgentRun` as the execution identity;
- Hermes API keys/secrets appear in source code or logs;
- cancellation leaves orphaned processes;
- timeout leaves orphaned processes;
- stdout/stderr handling can deadlock;
- normal CI requires an external LLM;
- model routing from Phase 18.1.1 is bypassed;
- `HermesExecution` is accidentally deleted or semantically repurposed;
- OpenViking becomes a runtime hard dependency;
- tests are made weaker to obtain green results.

---

# 30. Documentation

Create:

```text
.ai/context/phase-18.1.3-hermes-agent-runtime.md
```

Document:

1. Runtime architecture.
2. `IAgentRuntime` contract.
3. `HermesAgentRuntime`.
4. Hermes process/session mapping.
5. configuration.
6. cancellation/timeout behavior.
7. output/error handling.
8. security considerations.
9. testing strategy.
10. distinction between AgentRun and HermesExecution.
11. what remains for Phase 18.1.4+.

Include the architecture:

```text
Factory
  │
  ├── AgentDefinition
  │
  └── AgentRun
          │
          ▼
    IAgentRuntime
          │
          ▼
  HermesAgentRuntime
          │
          ▼
       Hermes
```

---

# 31. Final Report

At completion provide:

1. GO / NO-GO.
2. Files created/modified.
3. Final `IAgentRuntime` contract.
4. Hermes runtime implementation.
5. Hermes configuration.
6. Process/session strategy.
7. Timeout/cancellation strategy.
8. Error handling.
9. Tests added.
10. Commands actually executed.
11. Actual test/build results.
12. Whether real Hermes smoke test was executed.
13. Any limitations.
14. Recommended Phase 18.1.4.

Never claim a real Hermes execution succeeded unless it was actually executed.

Do not commit or push changes unless explicitly instructed.

---

# Definition of Done

Phase 18.1.3 is complete only when:

```text
AgentDefinition
       ↓
    AgentRun
       ↓
 IAgentRuntime
       ↓
 HermesAgentRuntime
       ↓
 Hermes
```

is a real, cleanly separated architecture.

The Factory owns durable AgentRun identity and state.

Hermes owns agent execution.

The runtime is:

- configurable;
- cancellable;
- timeout-safe;
- observable;
- testable without external LLM access;
- provider/model-routing compatible;
- secure;
- isolated behind `IAgentRuntime`.

The result must be a stable foundation for:

```text
Phase 18.1.4 → Agent Tools + Agent Memory
Phase 18.1.5 → Agent Orchestration Service + Worker
Phase 18.1.6 → Agent API + Metrics
Phase 18.1.7 → End-to-End Agent Execution + Stability Gate
```