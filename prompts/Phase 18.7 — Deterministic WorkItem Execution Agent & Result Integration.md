# Phase 18.7 — Deterministic WorkItem Execution Agent & Result Integration

## 1. Phase Objective

Build the next missing production loop in AI Factory:

```text
Project
  -> persisted ProjectPlan
  -> Ready WorkItems
  -> TaskDispatchScheduler
  -> AgentRun
  -> AgentDefinition for WorkItemType
  -> Hermes / IAgentRuntime
  -> StructuredAgentResult
  -> deterministic WorkItem execution result handling
  -> WorkItem Testing / Completed / Failed / Repairing / Reviewing
```

Phase 18.5 already provides the authoritative persisted Project DAG.

Phase 18.6 already provides the LLM Planner that creates that DAG.

Phase 18.3 already provides deterministic scheduling/dispatch.

Phase 18.4 already provides AgentRun heartbeat and recovery.

Phase 18.1.x already provides AgentRun execution, structured results, Hermes integration, and lifecycle persistence.

**This phase must connect the existing WorkItem execution path into a deterministic, explicit execution-result contract.**

The objective is NOT to redesign the scheduler, AgentRun lifecycle, Hermes runtime, Planner, or ProjectPlanService.

The Factory must remain authoritative:

> LLM output is untrusted execution input/result data.  
> AI Factory decides whether and how a WorkItem changes state.

---

# 2. Mandatory Repository Inspection

Before changing code, inspect the current repository and specifically trace the complete existing path:

- `Project`
- `ProjectStatus`
- `ProjectTransitions`
- `WorkItem`
- `WorkItemStatus`
- `WorkItemType`
- `WorkItemDependency`
- `AgentRun`
- `AgentRunStatus`
- `AgentRunTransitions`
- `AgentDefinition`
- `IAgentRuntime`
- `AgentOrchestrator`
- `AgentRunWorker`
- `AgentRunWorkItemSync`
- `TaskDispatchScheduler`
- `TaskReadinessService`
- `TaskStateManager`
- `IAgentRunRepository`
- `IAgentRunService`
- `StructuredAgentResult`
- `IStructuredAgentResultParser`
- `StructuredAgentResultParser`
- `AgentExecutionInstruction`
- `IAgentInstructionBuilder`
- `AgentInstructionBuilder`
- Phase 18.5 `ProjectPlanService`
- Phase 18.6 `ProjectPlanner`
- `PlannerResultMapper`
- all existing WorkItem/AgentRun tests
- API and Worker composition roots
- existing real Hermes E2E tests
- existing execution/orchestration code from before Phase 18.1

Also inspect `AGENTS.md` and the repository's existing conventions before implementation.

Do not assume the current WorkItem execution path is incomplete until the actual implementation has been traced.

Produce a short internal implementation plan based on the repository's real state before editing.

---

# 3. Architecture Boundary

The resulting architecture should remain:

```text
TaskDispatchScheduler
        |
        v
     AgentRun
        |
        v
 AgentRunWorker
        |
        v
 AgentOrchestrator
        |
        v
    IAgentRuntime
        |
        v
 HermesAgentRuntime
        |
        v
      Hermes
        |
        v
 StructuredAgentResult
        |
        v
 deterministic WorkItem result handling
        |
        v
 WorkItem state transition
```

Do NOT introduce:

- a second scheduler
- a second AgentRun lifecycle
- a second Hermes integration
- direct Hermes invocation from the API
- direct OpenRouter/OmniRoute invocation
- a second structured-result envelope
- a second dependency resolver
- a second WorkItem state machine
- a second Project state machine

Reuse the existing infrastructure.

---

# 4. First Determine the Existing Gap

Before implementing anything, answer from the code:

1. When `TaskDispatchScheduler` dispatches a Ready WorkItem, exactly how is the AgentRun created?
2. How is the WorkItem's objective/input transferred into `AgentRun.Input`?
3. How is `AgentDefinition` selected from `WorkItemType`?
4. What happens when the AgentRun succeeds?
5. What happens when it fails?
6. Does `AgentRunWorkItemSync` currently use only AgentRun terminal status, or does it understand `StructuredAgentResult`?
7. Can a successful AgentRun currently produce:
   - `Testing`
   - `Completed`
   - `Reviewing`
   - `Repairing`
   - or only one generic terminal WorkItem state?
8. Is there already an execution result contract that can be reused?
9. What information is currently persisted from the coding agent's response?
10. What is the smallest missing deterministic layer?

**Do not implement functionality that already exists.**

The implementation must be based on the actual repository state.

---

# 5. WorkItem Execution Contract

If the repository does not already contain an adequate contract, introduce a minimal execution-specific interpretation of the existing `StructuredAgentResult.Data`.

Do NOT modify `StructuredAgentResult` itself unless absolutely required.

The expected WorkItem execution result should be conceptually:

```json
{
  "execution": {
    "version": 1,
    "status": "completed",
    "summary": "...",
    "artifacts": [],
    "tests": [],
    "notes": []
  }
}
```

The exact final shape must follow existing project conventions discovered during repository inspection.

Important:

- `StructuredAgentResult.CurrentVersion` remains the envelope version.
- Execution payload version is independent.
- Do not reuse Planner's `plan.version`.
- Do not allow arbitrary LLM fields to directly mutate domain state.
- The execution mapper must be deterministic.

---

# 6. Execution Result Mapping Boundary

If a mapper is needed, create a focused abstraction such as:

```text
IWorkItemExecutionResultMapper
WorkItemExecutionResultMapper
```

Its only responsibility is:

```text
StructuredAgentResult
        ->
validated execution result
```

It must NOT:

- persist WorkItems
- create AgentRuns
- schedule tasks
- resolve dependencies
- transition Projects
- execute tools
- invoke Hermes
- invoke an LLM

The mapper should reject structurally invalid execution results deterministically.

Distinguish at least:

```text
RuntimeFailed
CandidateExecutionResultInvalid
ExecutionSucceeded
ExecutionRequestedRepair
```

Use existing domain terminology where equivalent concepts already exist.

Do not invent unnecessary result categories if the existing domain already has the required semantics.

---

# 7. WorkItem State Transition Policy

The Factory must remain authoritative over WorkItem state.

Do not allow an LLM to directly specify arbitrary WorkItemStatus values.

Instead, define an explicit deterministic policy based on:

1. AgentRun terminal state
2. Structured execution result validity
3. Existing WorkItem lifecycle rules
4. Existing WorkItem transitions

For example, depending on the actual existing state machine:

```text
AgentRun Failed/TimedOut/Cancelled
    -> WorkItem Failed

AgentRun Succeeded
    + invalid execution result
    -> WorkItem Failed

AgentRun Succeeded
    + execution result = completed
    -> WorkItem Completed OR Testing
       according to the existing domain semantics

AgentRun Succeeded
    + execution result = repair_required
    -> WorkItem Repairing
```

Do not blindly use the above states.

**Inspect the existing lifecycle and select the smallest policy compatible with it.**

The key requirement is:

> A successful LLM process must not automatically mean successful software delivery.

If the repository already distinguishes implementation from testing/reviewing, preserve that distinction.

---

# 8. Coding Agent Objective

WorkItem AgentRuns need a deterministic execution objective.

Do NOT create a new prompt-building framework.

Reuse:

```text
IAgentInstructionBuilder
AgentInstructionBuilder
AgentExecutionInstruction
```

The WorkItem input/objective should contain enough context for the agent to execute the specific WorkItem.

At minimum, if available in the current model:

- Project name
- Project description
- WorkItem title
- WorkItem description
- WorkItem type
- priority
- stable `PlanKey`
- relevant dependency context
- explicit execution constraints
- required structured result shape

The objective must clearly state:

- this is an implementation/execution task
- the agent must work only on the assigned WorkItem
- it must not silently expand scope
- it must report what it actually changed
- it must report tests actually executed
- it must not fabricate test results
- it must return the required structured result
- it must not claim success merely because code was generated

Do not add broad repository/project context mechanisms in this phase.

---

# 9. AgentDefinition Selection

Reuse the AgentDefinition selection established by Phase 18.3/18.6.

Do not introduce a new agent registry.

For a WorkItem:

```text
WorkItemType
    ->
compatible AgentDefinition
    ->
AgentRun
```

If the existing scheduler already performs this selection, leave it unchanged.

If the current path has a gap, add only the minimal missing deterministic logic.

Planner AgentDefinitions (`AgentType = "Planner"`) must never accidentally be selected as WorkItem execution agents.

The distinction between:

```text
Planner Agent
```

and:

```text
WorkItem execution Agent
```

must remain explicit.

---

# 10. AgentRun / WorkItem Correlation

Every WorkItem execution AgentRun must preserve:

```text
AgentRun.ProjectId = WorkItem.ProjectId
AgentRun.WorkItemId = WorkItem.Id
```

Planner AgentRuns remain:

```text
ProjectId = project.Id
WorkItemId = null
```

Do not alter the Phase 18.6 Planner semantics.

Ensure execution result processing can unambiguously identify the WorkItem from `AgentRun.WorkItemId`.

Do not use title, PlanKey, or prompt text as the primary correlation mechanism.

---

# 11. Idempotency and Race Safety

Result handling must be safe if the same terminal AgentRun is observed more than once.

The operation:

```text
AgentRun terminal
    -> WorkItem result synchronization
```

must be effectively idempotent.

A duplicate worker observation must not:

- create another WorkItem
- create another AgentRun
- regress a terminal WorkItem
- re-run a transition incorrectly
- overwrite a newer result
- resurrect a cancelled/failed WorkItem

Reuse the existing optimistic concurrency/version mechanism.

Do NOT introduce distributed locks.

Do NOT introduce static in-memory ownership.

---

# 12. Failure Classification

Keep these failure categories distinct:

### Runtime failure

The AgentRun itself failed, timed out, or was cancelled.

Example:

```text
AgentRun = TimedOut
```

This is not an invalid structured candidate.

### Execution result invalid

AgentRun succeeded, but its structured result cannot be interpreted safely.

Example:

```text
AgentRun = Succeeded
StructuredResult = malformed
```

### Execution semantic failure

The agent successfully returned a structurally valid result indicating that the requested implementation could not be completed.

### Successful execution

The structured result is valid and indicates completion according to the agreed execution contract.

Do not collapse these into a generic exception.

Use existing exception/result conventions where possible.

---

# 13. Testing Boundary

This phase must NOT attempt to prove that generated code is correct.

The purpose is to prove deterministic orchestration behavior.

Tests must establish:

### Unit tests

For the execution result mapper:

- valid result
- unsupported execution version
- missing execution object
- missing status
- malformed JSON
- malformed result fields
- missing required fields
- invalid status
- empty result
- deterministic mapping

### Application integration tests

Using:

```text
real SQLite
real AgentRun
real WorkItem
real AgentOrchestrator
FakeAgentRuntime
real result handling
```

Prove:

1. Ready WorkItem is dispatched.
2. AgentRun is linked to the WorkItem.
3. Fake runtime returns a valid successful execution result.
4. Structured result is persisted.
5. WorkItem transitions according to the deterministic policy.
6. Invalid execution result produces no unsafe state mutation.
7. Runtime failure produces deterministic WorkItem failure handling.
8. Duplicate terminal-result processing is safe.
9. Existing dependency/readiness behavior remains correct.

Use the real existing `TaskDispatchScheduler` where appropriate.

Do not replace the scheduler with a fake merely to simplify the test.

---

# 14. Regression Tests

Do NOT rewrite existing Phase 18.1–18.6 tests.

Do NOT duplicate:

- Planner tests
- ProjectPlanService tests
- AgentRun lifecycle tests
- heartbeat tests
- recovery tests
- scheduler tests

Add only focused regression coverage for the new execution-result behavior.

The full suite must still pass.

---

# 15. Real Hermes E2E

If the existing opt-in Hermes infrastructure supports this phase, add **one focused opt-in test**.

Reuse:

```text
AI_FACTORY_HERMES_SMOKE_TEST=true
```

and the existing local OmniRoute configuration.

The test should prove:

```text
Ready WorkItem
 -> real AgentRun
 -> real Hermes
 -> OmniRoute
 -> StructuredAgentResult
 -> execution result mapping
 -> persisted WorkItem state
```

The test must use a deterministic marker such as:

```text
AI_FACTORY_WORKITEM_EXECUTION_E2E_OK
```

in the test-only objective augmentation, following the same anti-cache/fabrication principle already established in Phase 18.6.

Do not claim the real E2E passed if the environment variable is not enabled.

If not run:

```text
Real WorkItem Execution E2E: NOT RUN
```

Report honestly.

Do not make the entire phase dependent on real LLM availability.

---

# 16. API Scope

Do not add a new synchronous execution endpoint.

The existing asynchronous model remains:

```text
API
 -> scheduler / AgentRun
 -> Worker
 -> Hermes
```

If the existing API already exposes sufficient AgentRun and WorkItem state, do not change it.

Only add API surface if a concrete observability gap is discovered.

If an endpoint is necessary, keep it read-only or asynchronous.

Never execute Hermes directly inside an HTTP request.

---

# 17. Project Lifecycle

Do not redesign Project lifecycle.

Do not automatically invent:

```text
Planning -> Implementation
```

or:

```text
Implementation -> Testing
```

unless the existing domain and current scheduler architecture explicitly require it for correctness.

If automatic Project transitions are currently intentionally controlled by the caller, preserve that behavior.

This phase is about WorkItem execution, not Project workflow redesign.

---

# 18. Scheduler Boundary

The scheduler remains responsible for:

- finding Ready WorkItems
- checking dependencies/readiness
- deterministic ordering
- concurrency limits
- creating/dispatching AgentRuns

The execution result layer is responsible for:

- interpreting the completed AgentRun
- validating execution result
- updating the WorkItem according to the existing lifecycle

Do not move scheduling responsibilities into the result handler.

Do not make the result handler dispatch dependent WorkItems directly.

After a WorkItem becomes eligible, the existing scheduler should discover it naturally on its next cycle.

---

# 19. No New Persistence Unless Necessary

Prefer **zero migrations**.

Before adding a database field, prove that the existing model cannot represent the required state.

Existing fields such as:

- `AgentRun.StructuredResult`
- `AgentRun.Status`
- `AgentRun.ProjectId`
- `AgentRun.WorkItemId`
- `WorkItem.Status`
- `WorkItem.Version`
- existing timestamps/heartbeat fields

should be reused.

Only introduce a migration if a genuinely missing persistent invariant is discovered.

If no migration is needed, verify:

```text
dotnet ef migrations has-pending-model-changes
```

and report the result.

---

# 20. Explicit Non-Goals

Do NOT implement:

- new Planner functionality
- Planner re-planning/versioning
- Project Specification persistence
- Project Architecture persistence
- multi-agent orchestration
- agent delegation
- OpenViking integration
- learning/RL
- model-selection framework
- automatic model routing redesign
- Git automation redesign
- CI/CD redesign
- repository-wide code indexing
- vector search
- autonomous test-repair loops
- code review agent
- security review agent
- UI
- distributed locks
- new scheduler
- new Hermes runtime
- new OpenRouter/OmniRoute integration
- broad API redesign

Those belong to later phases.

---

# 21. Security / Trust Boundary

Treat all LLM-generated execution data as untrusted.

Never allow the LLM to directly provide:

- database identifiers
- arbitrary WorkItem IDs
- arbitrary Project IDs
- authorization decisions
- unrestricted status transitions
- scheduler decisions
- dependency mutations

The authoritative identifiers come from the persisted AgentRun/WorkItem relationship.

The LLM may report facts about its execution; the Factory decides what those facts mean for domain state.

---

# 22. Empirical Verification Gates

Run, at minimum:

```text
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
dotnet ef migrations has-pending-model-changes --project ../AI.Factory.Infrastructure --startup-project .
```

Also run focused tests for:

```text
WorkItem execution result mapping
WorkItem execution integration
AgentRun -> WorkItem synchronization
```

If the repository has existing deterministic test scripts/commands, use them rather than inventing replacements.

The final report must provide exact observed counts.

Never fabricate:

- test results
- Hermes execution
- LLM responses
- migration state
- runtime behavior

---

# 23. Definition of Done

Phase 18.7 is **GO** only if all of the following are true:

- [ ] Existing WorkItem execution path was fully inspected before changes.
- [ ] The smallest real missing execution layer was identified.
- [ ] Existing scheduler remains authoritative for dispatch.
- [ ] Existing AgentRun lifecycle remains authoritative.
- [ ] Existing Hermes runtime remains unchanged unless a proven integration bug requires otherwise.
- [ ] WorkItem execution uses the existing AgentRun → Hermes pipeline.
- [ ] WorkItem AgentRuns retain ProjectId + WorkItemId correlation.
- [ ] Structured execution result is validated deterministically.
- [ ] LLM output cannot directly choose arbitrary domain state transitions.
- [ ] Runtime failure and invalid execution result remain distinct.
- [ ] Successful execution does not falsely imply software correctness.
- [ ] Duplicate result handling is safe.
- [ ] Existing dependency/readiness behavior remains intact.
- [ ] No unnecessary migration was introduced.
- [ ] Debug build passes.
- [ ] Release build passes.
- [ ] Full existing test suite passes.
- [ ] Focused new tests pass.
- [ ] Real Hermes E2E is either genuinely passed or honestly reported as NOT RUN.
- [ ] No existing Phase 18.1–18.6 behavior was unnecessarily rewritten.
- [ ] No commit/push is performed.

---

# 24. Required Final Report

Create:

```text
.ai/context/phase-18.7-workitem-execution.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Existing WorkItem Execution Path
4. Exact Gap Identified
5. Architecture After Phase 18.7
6. Execution Result Contract
7. Result Mapping / Validation Boundary
8. WorkItem State Transition Policy
9. Idempotency / Concurrency Behavior
10. Tests Added
11. Exact Verification Results
12. Real Hermes E2E Result
13. Migration Status
14. Known Limitations
15. Remaining Risks
16. Final Decision

The report must distinguish:

```text
VERIFIED
NOT RUN
KNOWN LIMITATION
RISK
```

Do not describe an unexecuted real-Hermes test as passed.

---

# 25. Final Decision

At the end of the implementation, explicitly state one of:

```text
WorkItem Execution Integration: VERIFIED
Final Decision: GO
```

or:

```text
WorkItem Execution Integration: NOT VERIFIED
Final Decision: NO-GO
```

A GO decision requires empirical evidence from the repository, not merely successful compilation.

Do not commit or push anything.