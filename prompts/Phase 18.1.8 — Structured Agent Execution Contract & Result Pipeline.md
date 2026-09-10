# Phase 18.1.8 — Structured Agent Execution Contract & Result Pipeline

## Phase Objective

Build the first production-grade structured execution contract between AI Factory and Hermes.

Phase 18.1.7 already proved the real execution path:

API → AgentRun → Worker → Orchestrator → HermesAgentRuntime → Hermes Docker → OmniRoute → real LLM → persisted result.

Do NOT rework or redesign that infrastructure.

The objective of this phase is to evolve the currently raw AgentRun input/output model into a controlled, validated, structured agent execution contract that will become the foundation for future Planner, Architect, Developer, Tester, Reviewer and other specialized agents.

The Factory must remain the workflow/state-machine owner.

Hermes remains the agent runtime.

OpenViking remains optional and MUST NOT become a hard dependency in this phase.

---

# 1. Repository Inspection

Before modifying anything:

1. Read `AGENTS.md`.
2. Inspect the current solution/project structure.
3. Inspect the current Agent domain:
   - AgentRun
   - AgentDefinition
   - AgentRunStatus
   - lifecycle transition validation
4. Inspect:
   - IAgentRuntime
   - HermesAgentRuntime
   - HermesAgentExecutor
   - IAgentOrchestrator
   - AgentOrchestrator
   - AgentRunWorker
5. Inspect current:
   - Agent API
   - repositories
   - metrics
   - persistence
   - migrations
   - existing Hermes E2E tests
6. Read the Phase 18.1.7 report.
7. Preserve all existing architectural decisions.

Do not recreate functionality that already exists.

---

# 2. Scope

Implement a structured contract for agent execution.

The contract must distinguish at least:

### Execution request

A request sent by the Factory to an agent should have explicit concepts for:

- objective
- instructions
- project/context identifier where applicable
- execution constraints
- expected output format
- allowed capabilities/tools
- optional previous context
- correlation/idempotency information

Do not add unnecessary fields merely for future speculation.

### Execution result

The Factory must be able to distinguish:

- successful execution
- failed execution
- cancelled execution
- timed-out execution
- malformed/invalid agent output

The result should contain structured information rather than relying only on an arbitrary text blob.

---

# 3. Structured Output Contract

Introduce a versioned structured agent result contract.

At minimum support:

- contract/version identifier
- status
- summary
- textual response/output
- structured data payload where applicable
- warnings
- errors
- produced artifact references
- execution metadata

The design must allow future agents to return specialized structured payloads without changing the core AgentRun entity every time.

Prefer a typed envelope plus JSON payload rather than putting every future agent-specific field directly into AgentRun.

Example conceptual shape:

AgentExecutionResult
    Version
    Status
    Summary
    Output
    Data
    Warnings
    Errors
    Artifacts
    Metadata

The exact names should follow existing repository conventions.

---

# 4. Validation

Add centralized validation for structured agent results.

The Factory must never silently accept malformed structured output.

Validation must distinguish:

1. Valid successful result
2. Valid failure result
3. Invalid/malformed result
4. Missing required result
5. Unsupported contract version

Do not make validation dependent on a specific LLM provider.

Do not trust the LLM simply because it returned JSON.

---

# 5. AgentRun Integration

Integrate the structured result into AgentRun persistence without breaking the existing lifecycle.

Requirements:

- preserve existing AgentRun status semantics
- preserve optimistic concurrency
- preserve idempotent creation
- preserve Worker claiming behavior
- preserve existing failure/cancellation/timeout handling
- preserve existing metrics
- preserve existing Hermes runtime abstraction

Do not change `IAgentRuntime` unless absolutely required.

If a change is genuinely required, keep it minimal and explain why.

The Factory remains authoritative for AgentRun lifecycle state.

---

# 6. Prompt / Instruction Construction

Create a dedicated application-level component responsible for constructing the execution instructions sent to an agent.

Do NOT build prompts directly inside the Worker.

Conceptually:

AgentRun
    ↓
AgentExecutionRequest
    ↓
Instruction/Prompt Builder
    ↓
IAgentRuntime
    ↓
Hermes

The prompt builder should make the contract explicit:

- objective
- rules
- expected output
- constraints
- available context
- output contract

Avoid hard-coding future Planner/Developer/Reviewer behavior.

This is infrastructure for those future roles, not those roles themselves.

---

# 7. Hermes Integration

Adapt the existing Hermes path only as necessary to carry the new structured execution request.

Do NOT:

- redesign HermesAgentExecutor
- redesign HermesAgentRuntime
- replace OmniRoute
- add OpenViking
- add multi-agent delegation
- add unrestricted shell/file/browser tools
- introduce another runtime

The real Hermes execution path from Phase 18.1.7 must remain intact.

The existing OmniRoute test configuration must continue to work.

---

# 8. Persistence

Determine the minimum persistence changes required.

Possible approaches include:

- structured JSON columns
- dedicated result table
- versioned result records

Choose the simplest design that preserves:

- queryability
- future extensibility
- backward compatibility
- SQLite compatibility
- EF Core migrations
- deterministic testing

Do not introduce a complex event-sourcing architecture.

If a migration is required:

1. create it
2. inspect generated SQL/model snapshot
3. verify upgrade from an existing database
4. verify clean database creation
5. verify existing AgentRun data preservation

---

# 9. API

Expose structured execution results through the existing AgentRun API where appropriate.

Do not create a second parallel AgentRun API.

The API should make it possible to inspect:

- AgentRun lifecycle status
- execution summary
- structured result
- warnings/errors
- produced artifact references

Keep API DTOs separate from persistence/domain models.

Do not synchronously execute Hermes from the API.

---

# 10. Tests

Add deterministic tests for:

### Contract

- valid request
- invalid request
- valid result
- invalid result
- unsupported version
- malformed JSON payload
- missing required fields

### Persistence

- structured result persistence
- result retrieval
- existing AgentRun compatibility
- migration/clean database if migration exists

### Orchestrator

- successful structured result
- malformed result
- runtime failure
- timeout
- cancellation
- lifecycle persistence

### API

- structured result returned correctly
- errors represented correctly
- no synchronous Hermes invocation

### Regression

All existing tests must continue passing.

---

# 11. Real Hermes Validation

Do not replace the real Hermes tests with mocks.

Extend the existing real-Hermes E2E path with ONE deterministic structured-output smoke test.

The real model must be instructed to return the agreed contract.

Use a deterministic marker such as:

`AI_FACTORY_STRUCTURED_RESULT_OK`

The Factory must:

1. create a real AgentRun
2. queue it
3. Worker claims it
4. Orchestrator executes it
5. Hermes runs in Docker
6. Hermes reaches OmniRoute
7. real LLM executes
8. structured result is returned
9. Factory validates it
10. Factory persists it
11. API/repository can read it back

Do not claim success based on a fake runtime.

---

# 12. Regression / Stability Gate

Run:

1. targeted new tests
2. existing Agent/Worker/API tests
3. full test suite
4. Debug build
5. Release build
6. one real structured Hermes E2E execution
7. existing Phase 18.1.7 real-Hermes stability suite

Do NOT unnecessarily repeat expensive real-Hermes 5-run stability tests if the implementation does not touch that execution path materially. If it does touch the relevant path, rerun the minimum existing stability gate required to prove no regression.

Never fabricate test results.

---

# 13. Explicit Non-Goals

Do NOT implement in this phase:

- Planner Agent
- Architect Agent
- Developer/Coding Agent
- Tester Agent
- Reviewer Agent
- multi-agent orchestration
- DAG/task planning
- OpenViking integration
- Git automation
- automatic code modification
- automatic commits
- automatic pull requests
- autonomous deployment
- browser automation
- unrestricted shell tools
- long-term agent memory redesign
- learning/experience system

Those belong to later phases.

---

# 14. Security Constraints

The structured result must be treated as untrusted LLM output.

Never allow the result itself to automatically authorize:

- shell commands
- arbitrary filesystem access
- Git operations
- deployment
- credential access
- network access

Capabilities must continue to be controlled by the Factory/tool layer.

---

# 15. Documentation

Create/update a phase report:

`.ai/context/phase-18.1.8-structured-agent-contract.md`

Document:

- architecture
- new contracts
- persistence decisions
- validation rules
- API changes
- tests
- real Hermes evidence
- migration evidence if applicable
- known limitations
- next-phase recommendation

---

# 16. Definition of Done

The phase is GO only if:

- structured AgentExecutionRequest exists
- structured AgentExecutionResult exists
- result contract is versioned
- invalid/malformed output is rejected
- AgentRun lifecycle remains correct
- existing Hermes architecture remains intact
- OmniRoute real-Hermes execution still works
- structured result is persisted and retrievable
- API exposes the structured result correctly
- deterministic tests pass
- full regression passes
- Debug build passes with 0 warnings/errors
- Release build passes with 0 warnings/errors
- real structured Hermes E2E passes
- no security boundary was weakened
- no unnecessary future architecture was introduced

Final report MUST explicitly state:

`Structured Agent Contract: VERIFIED / NOT VERIFIED`

and

`Final Decision: GO / NO-GO`

Do not commit or push unless explicitly instructed.
Do not claim evidence that was not actually executed.