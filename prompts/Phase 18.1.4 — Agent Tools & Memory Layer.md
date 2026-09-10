# Phase 18.1.4 — Agent Tools & Memory Layer

## Phase Objective

Phase 18.1.3 is COMPLETE with a GO decision.

The current architecture is:

```text
AgentDefinition
       ↓
    AgentRun
       ↓
 IAgentRuntime
       ↓
HermesAgentRuntime
       ↓
 IAgentExecutor
       ↓
 HermesAgentExecutor
       ↓
 Hermes / Docker
```

Phase 18.1.3 successfully established the real `IAgentRuntime` boundary and deterministic runtime testing.

The next objective is to establish the **Agent Tool and Agent Memory layer**.

This phase must provide clean, runtime-agnostic contracts and minimal production-ready implementations that can later be consumed by the Agent Orchestrator.

The architecture must remain:

```text
AgentRun
   │
   ├──────────────┐
   ▼              ▼
IAgentRuntime   Agent Context
                  │
             ┌────┴────┐
             ▼         ▼
        IAgentTool  IAgentMemory
             │         │
             ▼         ▼
        Tool providers
                   Memory providers
```

Do NOT turn OpenViking into a mandatory dependency in this phase.

---

# 1. Repository Inspection

Before modifying anything:

1. Read `AGENTS.md`.
2. Read the Phase 18.1.2 and Phase 18.1.3 documentation.
3. Inspect:
   - `IAgentTool`
   - `IAgentMemory`
   - `IAgentRuntime`
   - `AgentDefinition`
   - `AgentRun`
   - `HermesAgentRuntime`
   - existing context abstractions
   - `IContextProvider`
   - `LocalContextProvider`
   - existing artifact/context models
   - existing configuration
   - existing logging/metrics
   - existing tests
   - any current tool abstractions or tool execution code
4. Search the repository for:
   - `IAgentTool`
   - `IAgentMemory`
   - `IContextProvider`
   - `OpenViking`
   - `ContextProvider`
   - `Tool`
   - `Memory`
5. Determine which existing abstractions can be reused.

Do not create duplicate abstractions when an existing abstraction already provides the same responsibility.

---

# 2. Architectural Goal

Establish these boundaries:

```text
IAgentTool
IAgentMemory
```

They must be application/domain-facing contracts.

Infrastructure implementations may depend on:

- filesystem
- database
- external services
- OpenViking in a later provider

But Domain/Application must NOT directly depend on:

- OpenViking SDK
- HTTP clients for OpenViking
- Hermes internals
- OpenRouter
- Docker
- filesystem implementation details

---

# 3. IAgentTool Contract

Review the existing `IAgentTool`.

If it is only a placeholder, evolve it into a useful but minimal abstraction.

A tool should conceptually contain:

```text
Name
Description
Input schema
Execution
```

The exact shape should follow existing project conventions.

The contract must support:

```text
Tool discovery
Tool invocation
Structured input
Structured result
Cancellation
Error reporting
```

Avoid making the tool abstraction Hermes-specific.

The same tool must conceptually be usable by:

```text
Hermes
Claude
Codex
another runtime
```

without changing the domain contract.

---

# 4. Tool Descriptor

Introduce a stable descriptor if one does not already exist.

Conceptually:

```text
AgentToolDescriptor
    Id / Name
    Description
    InputSchema
    Enabled
```

The schema should be machine-readable.

Do not introduce a large JSON-schema framework unless the repository already uses one.

Keep the initial representation simple and extensible.

---

# 5. Tool Execution Contract

The execution contract should support something conceptually equivalent to:

```text
ExecuteAsync(
    ToolExecutionRequest,
    CancellationToken
)
```

The request should contain:

- Tool name/id
- AgentRunId
- AgentDefinitionId where available
- CorrelationId
- structured input
- cancellation token

The result should contain:

- success/failure
- structured output
- error information
- duration
- safe diagnostic metadata

Do not persist arbitrary tool input/output automatically.

---

# 6. Tool Registry

Create a tool registry abstraction if one does not already exist.

Conceptually:

```text
IAgentToolRegistry
```

Responsibilities:

- register tools
- resolve a tool by name
- enumerate available tools
- expose descriptors
- respect enabled/disabled state

The registry must reject duplicate tool names deterministically.

Tool names should be stable identifiers.

Do not make the registry specific to Hermes.

---

# 7. Initial Built-in Tool

Implement only a **minimal safe tool set** in this phase.

Do NOT build an unrestricted shell tool.

Do NOT expose arbitrary OS command execution.

Do NOT give agents unrestricted filesystem access.

A suitable first tool may be a deterministic/read-only tool already supported by the existing architecture, such as:

```text
GetFile
```

or:

```text
ReadArtifact
```

depending on what already exists in the repository.

If an existing safe tool abstraction already exists, reuse it instead of introducing a new tool solely for this phase.

The first tool must:

- have a clear input schema;
- validate input;
- respect cancellation;
- enforce path/scope restrictions if filesystem access is involved;
- never escape the configured workspace;
- have deterministic tests.

If no safe existing tool can be implemented without introducing unnecessary scope, implement the registry and contracts only and use a deterministic test tool.

---

# 8. Tool Security Boundary

Treat tools as privileged capabilities.

Every tool implementation must explicitly define:

```text
What it can access
What input it accepts
What side effects it can perform
```

For filesystem tools:

- prevent `..` traversal;
- resolve canonical paths;
- enforce workspace root;
- reject paths outside the workspace;
- never expose arbitrary host paths.

Do not expose:

- `.env`
- credentials
- SSH keys
- cloud credentials
- application secrets

through a generic file-reading tool.

---

# 9. IAgentMemory Contract

Review the existing `IAgentMemory`.

Establish a minimal provider-neutral contract.

Memory should conceptually support:

```text
Search
Get
Store
Delete/expire where appropriate
```

The exact operations should follow actual repository needs.

Do not create a vector-database-specific abstraction.

Do not expose OpenViking request/response types.

The contract should work with:

```text
LocalMemory
OpenVikingMemory
future providers
```

without changing Agent orchestration code.

---

# 10. Memory Model

Separate memory from Factory transactional state.

The following remain in the Factory database:

```text
Project
WorkItem
AgentDefinition
AgentRun
Execution state
Approvals
Artifacts
Dependencies
```

Memory is contextual information used to help an Agent execute.

Do NOT move AgentRun state into memory.

Do NOT use memory as the source of truth for workflow state.

---

# 11. Memory Entry

Introduce a provider-neutral memory representation if one does not already exist.

At minimum evaluate:

```text
MemoryId
Scope
Type
Content
Metadata
CreatedAt
UpdatedAt
```

Potential scopes:

```text
Project
Agent
AgentRun
Global
```

Do not overdesign the taxonomy.

The implementation must make it possible later to map these concepts to OpenViking memory types without exposing OpenViking-specific terminology to the domain.

---

# 12. Memory Search

Define a deterministic search contract.

Conceptually:

```text
MemoryQuery
    Scope
    Query
    Limit
    Optional filters
```

Return:

```text
MemorySearchResult
    Memory
    Score
    Metadata
```

Do not pretend a local implementation has semantic/vector relevance if it does not.

If the initial local provider uses lexical matching, document that clearly.

---

# 13. Local Memory Provider

Implement a minimal local deterministic provider unless an equivalent existing implementation can be reused.

Possible implementations:

```text
SQLite
```

or:

```text
existing Factory persistence
```

or a small filesystem-based provider if that is more consistent with the current architecture.

Prefer existing persistence infrastructure.

Do not introduce another database.

The implementation must support:

- store
- retrieve
- search
- deterministic ordering
- cancellation

---

# 14. OpenViking Boundary

OpenViking must remain OPTIONAL.

Do NOT make this phase dependent on:

- OpenViking server availability
- OpenViking Docker
- external network
- OpenViking credentials

If useful, create an infrastructure boundary such as:

```text
OpenVikingMemoryProvider
```

but only if it can be implemented cleanly using the repository's existing HTTP/client conventions.

Otherwise defer the concrete OpenViking adapter to a later phase.

The important requirement now is that:

```text
IAgentMemory
```

is ready to support it later.

---

# 15. Memory Isolation

Memory operations must be scoped.

An AgentRun for Project A must not automatically retrieve private Project B memory.

Define and enforce appropriate scope semantics.

At minimum test:

```text
Project A memory
    ≠
Project B memory
```

unless explicitly requesting global memory.

Do not rely solely on callers to filter memory correctly.

The provider must enforce the requested scope.

---

# 16. AgentRun Context

Do not add large memory/tool payloads directly to AgentRun.

Instead provide an execution-context abstraction where appropriate.

Conceptually:

```text
AgentExecutionContext
    AgentRun
    Tools
    Memory
    Context
```

Keep this abstraction lightweight.

The future orchestrator should be able to assemble an Agent execution context without knowing whether memory comes from SQLite, OpenViking, or another provider.

---

# 17. Runtime Integration Boundary

Do NOT significantly rewrite `HermesAgentRuntime` in this phase.

The runtime currently works as:

```text
IAgentRuntime
    ↓
HermesAgentRuntime
```

Prepare it for future tool/memory injection, but do not implement a large Hermes tool protocol here.

If the existing runtime contract already supports tool/context collections, wire the registry/provider through that contract.

Otherwise leave a clean extension point for Phase 18.1.5.

Do not duplicate tool execution logic inside HermesAgentRuntime.

---

# 18. Dependency Injection

Register:

```text
IAgentToolRegistry
IAgentMemory
```

through Infrastructure/Application composition according to existing conventions.

Tests must be able to replace them with deterministic fakes.

Do not make the Domain layer aware of dependency injection.

---

# 19. Deterministic Test Doubles

Create deterministic test implementations where necessary:

```text
FakeAgentTool
FakeAgentToolRegistry
FakeAgentMemory
```

They must allow tests to simulate:

```text
tool success
tool failure
tool cancellation
memory store
memory search
memory isolation
```

No network access.

No Hermes dependency.

No OpenViking dependency.

No external LLM dependency.

---

# 20. Tool Tests

Add focused tests covering:

### Registry

- register tool
- resolve tool
- enumerate tools
- duplicate name rejected
- disabled tool not exposed

### Execution

- valid input
- invalid input
- successful result
- failure result
- cancellation
- deterministic output

### Security

If a filesystem/artifact tool is implemented:

- path traversal rejected
- outside-workspace path rejected
- secret files rejected where applicable

---

# 21. Memory Tests

Add focused tests covering:

### Store

- memory persisted
- metadata persisted
- timestamps populated

### Retrieve

- exact retrieval works
- missing memory handled correctly

### Search

- relevant entries returned
- limit respected
- deterministic ordering

### Scope isolation

- Project A cannot retrieve Project B private memory
- AgentRun-specific memory remains isolated
- global memory is available only when explicitly requested

### Cancellation

- cancellation is respected

---

# 22. Persistence

If local memory requires persistence:

1. use existing EF Core/SQLite infrastructure;
2. create a migration only if schema changes are actually necessary;
3. do not modify previous migration history;
4. validate clean database migration;
5. validate existing database upgrade.

Do not introduce a second SQLite database.

Do not store large unbounded payloads without an explicit size strategy.

---

# 23. Observability

Add minimal structured logging for:

```text
ToolExecutionStarted
ToolExecutionCompleted
ToolExecutionFailed
MemoryStore
MemorySearch
```

Safe identifiers only:

- AgentRunId
- CorrelationId
- ToolName
- MemoryScope

Do not log:

- secrets
- credentials
- complete sensitive memory content
- complete tool inputs by default
- complete tool outputs by default

If existing metrics infrastructure supports these operations cleanly, add the smallest useful counters/durations.

Do not redesign the metrics system in this phase.

---

# 24. Configuration

If tools or memory require configuration, introduce strongly typed options.

Do not hardcode:

- workspace root
- OpenViking endpoint
- credentials
- tool enablement

Use existing configuration conventions.

Do not add configuration for capabilities that are not implemented.

---

# 25. Architecture Verification

Verify:

```text
Domain
   ↑
Application
   ↑
Infrastructure
```

and specifically:

```text
Domain
  ├── no Hermes
  ├── no OpenRouter
  ├── no OpenViking
  └── no filesystem implementation

Application
  ├── can depend on IAgentTool
  ├── can depend on IAgentMemory
  └── cannot depend on OpenViking implementation

Infrastructure
  ├── tool implementations
  ├── memory implementations
  ├── Hermes integration
  └── future OpenViking adapter
```

---

# 26. Do NOT Implement Yet

Explicitly out of scope:

- Agent orchestration service
- Worker scheduling
- `/api/agents/*`
- autonomous retry logic
- DAG execution
- multi-agent delegation
- consensus
- project lifecycle automation
- full OpenViking integration if it requires substantial new infrastructure
- dashboard
- Agent UI
- unrestricted shell/terminal tool
- unrestricted filesystem tool
- browser tool
- coding tool
- autonomous code modification
- CI/CD redesign

These belong to later phases.

---

# 27. Regression Protection

Do not redo the Phase 18.1.3 implementation.

Do not repeat the 740-test suite multiple times unless a failure requires it.

Run:

1. new Tool tests;
2. new Memory tests;
3. affected Application/Infrastructure regression tests;
4. full regression once;
5. Release build.

Use actual executed results only.

---

# 28. Migration Gate

If no database schema is required, explicitly confirm:

```text
No migration required.
```

If a migration is required:

- create it normally;
- verify clean DB;
- verify upgrade from existing DB;
- verify no previous migration was modified;
- verify no data loss.

Do not use `dotnet ef migrations remove --force` against historical migrations.

Do not rewrite migration history.

---

# 29. Failure / NO-GO Conditions

Return **NO-GO** if:

- AgentRun becomes responsible for memory state;
- memory becomes the workflow source of truth;
- Application references OpenViking directly;
- tools become Hermes-specific;
- a generic tool exposes arbitrary host filesystem access;
- a tool can execute unrestricted shell commands;
- Project memory isolation is not enforced;
- external network access is required for deterministic tests;
- OpenViking becomes mandatory;
- secrets can appear in tool/memory logs;
- previous migrations are rewritten;
- existing Hermes runtime behavior is broken.

---

# 30. Documentation

Create:

```text
.ai/context/phase-18.1.4-agent-tools-memory.md
```

Document:

1. Tool architecture.
2. `IAgentTool`.
3. Tool registry.
4. Tool execution lifecycle.
5. Security boundaries.
6. Memory architecture.
7. Memory scopes.
8. Local memory implementation.
9. OpenViking boundary.
10. Runtime relationship.
11. Testing strategy.
12. What remains for orchestration.

Include:

```text
AgentRun
   │
   ├── IAgentRuntime
   │
   ├── IAgentToolRegistry
   │       └── IAgentTool[]
   │
   └── IAgentMemory
```

And explicitly state:

```text
Factory DB = workflow truth
Memory     = contextual knowledge
Hermes     = execution runtime
```

---

# 31. Final Report

At completion report:

1. GO / NO-GO.
2. Files created/modified.
3. Final `IAgentTool` contract.
4. Final tool registry contract.
5. Implemented tools.
6. Final `IAgentMemory` contract.
7. Memory provider implementation.
8. Scope/isolation strategy.
9. Whether OpenViking adapter was implemented or deferred.
10. DI registrations.
11. Migration status.
12. Tests added.
13. Commands actually executed.
14. Actual test/build results.
15. Architecture verification result.
16. Known limitations.
17. Recommended Phase 18.1.5.

Do not claim OpenViking integration unless it was actually implemented and tested.

Do not claim real Hermes integration beyond what was actually executed.

Do not commit or push changes unless explicitly instructed.

---

# Definition of Done

Phase 18.1.4 is complete only when AI Factory has a clean, provider-neutral Tool and Memory layer:

```text
                  AgentRun
                     │
          ┌──────────┴──────────┐
          ▼                     ▼
   IAgentToolRegistry       IAgentMemory
          │                     │
      IAgentTool[]        LocalMemoryProvider
          │                     │
          ▼                     ▼
     Tool execution       Contextual memory
```

The following must be true:

- tools are discoverable through a registry;
- tool execution is structured and cancellable;
- unsafe arbitrary host access is not exposed;
- memory is scoped and isolated;
- memory is not workflow state;
- deterministic local/fake implementations exist;
- no external service is required for CI;
- OpenViking remains an optional future provider;
- Hermes remains behind `IAgentRuntime`;
- Domain/Application remain provider-neutral.

The result becomes the foundation for:

```text
Phase 18.1.5 → Agent Orchestration Service + Worker
Phase 18.1.6 → Agent API + Metrics
Phase 18.1.7 → End-to-End Agent Execution + Stability Gate
```