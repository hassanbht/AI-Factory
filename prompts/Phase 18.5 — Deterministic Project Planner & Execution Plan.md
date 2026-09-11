# Phase 18.5 — Deterministic Project Planner & Execution Plan

## Phase Objective

Build the first real **Project Planning Engine** on top of the Project/WorkItem/Dependency/Scheduler infrastructure already verified through Phase 18.4.

The Planner must transform an approved Project specification/architecture context into a deterministic, executable **WorkItem DAG** that the existing:

```text
TaskDispatchScheduler
    ↓
AgentRun
    ↓
AgentRunWorker
    ↓
AgentOrchestrator
    ↓
IAgentRuntime / Hermes
```

pipeline can consume.

This phase is about **planning and task decomposition only**.

It must NOT implement the actual coding/test/review agents.

The result should be a persisted, validated, deterministic execution plan.

---

# 1. Mandatory Repository Inspection

Before changing anything:

1. Read `AGENTS.md`.
2. Inspect the current implementations of:
   - `Project`
   - `ProjectStatus`
   - `ProjectTransitions`
   - `WorkItem`
   - `WorkItemType`
   - `WorkItemStatus`
   - `WorkItemTransitions`
   - `WorkItemDependency`
   - `TaskDependencyService`
   - `ITaskDependencyResolver`
   - `TaskReadinessService`
   - `TaskDispatchScheduler`
   - `AgentRun`
   - `AgentDefinition`
   - `AgentRunWorker`
   - `AgentOrchestrator`
   - `AgentExecutionInstruction`
   - `StructuredAgentResult`
   - `AgentRunWorkItemSync`
   - existing Project/Task APIs
3. Inspect all existing Project/WorkItem tests.
4. Inspect any existing planning-related classes, interfaces, DTOs, or legacy planning/orchestration code.
5. Inspect the current database schema and migrations.
6. Read the reports from:
   - Phase 18.2
   - Phase 18.3
   - Phase 18.4
7. Search the entire repository for:
   - `Planning`
   - `Plan`
   - `Planner`
   - `TaskDependency`
   - `WorkItemType`
   - `AgentDefinition`
   - `AgentRun.Model`

Do not assume that a planning abstraction does not already exist.

---

# 2. Core Design Principle

The Planner is responsible for:

```text
Project
  ↓
Planning Input
  ↓
Plan Generation
  ↓
Plan Validation
  ↓
Persist WorkItems
  ↓
Persist Dependencies
  ↓
Ready WorkItems
  ↓
TaskDispatchScheduler
```

The Planner is NOT responsible for:

- executing tasks,
- calling Hermes directly,
- invoking `IAgentRuntime`,
- running tests,
- modifying source code,
- committing Git changes,
- reviewing implementation,
- repairing failed implementation.

The existing Scheduler remains the execution dispatcher.

---

# 3. Planning Model

First determine whether the current domain model is sufficient.

Prefer reusing:

```text
Project
WorkItem
WorkItemDependency
WorkItemType
WorkItemStatus
```

rather than creating a second task model.

If a persisted `Plan` entity is genuinely required, justify it before introducing it.

Do NOT create abstractions merely because a generic architecture might need them later.

The practical objective is:

> A Project must have a deterministic, persisted set of WorkItems and dependencies that represents its execution plan.

---

# 4. Planning Input Contract

Create an explicit application-level planning contract.

The Planner must receive enough information to construct a useful plan.

At minimum, identify:

- Project ID
- project name/title
- project description/objective
- specification/context if available
- architecture/context if available
- constraints
- expected technology/platform information if already represented by the domain

Do not invent fields that the current Project domain does not support.

If specification/architecture persistence does not yet exist, do NOT build the entire Specification/Architecture subsystem in this phase.

Instead:

1. inspect what is currently available,
2. define the smallest stable planning input abstraction,
3. document missing upstream inputs.

The Planner must be able to operate deterministically from the information it actually receives.

---

# 5. Deterministic Planning

The Planner must produce the same logical plan for the same planning input.

Do NOT let an LLM decide:

- task ordering,
- dependency correctness,
- cycle resolution,
- task readiness,
- duplicate handling,
- concurrency,
- execution priority.

These are Factory responsibilities.

If an LLM is used to generate candidate tasks, treat its output as **untrusted planning input**.

The Factory must validate and normalize it before persistence.

---

# 6. LLM Usage

Do not build a second agent runtime.

If the existing `IAgentRuntime` is used for Planner execution, use the already-established path:

```text
AgentRun
 → AgentRunWorker
 → AgentOrchestrator
 → IAgentRuntime
 → Hermes
```

Do not call Hermes directly from the Planner.

However, the Planner must not depend on successful LLM execution merely to prove the deterministic domain infrastructure.

Structure the system so that:

```text
candidate plan
    ↓
validation
    ↓
normalization
    ↓
persistence
```

is clearly separated from runtime execution.

If a deterministic planner can be implemented without an LLM at this stage, prefer that for the domain foundation.

---

# 7. Plan Contract

Define a versioned planning result contract.

The contract should represent:

```text
Plan
 ├── Version
 ├── Summary
 ├── Tasks[]
 │    ├── StableKey
 │    ├── Title
 │    ├── Description
 │    ├── Type
 │    ├── Priority
 │    └── DependsOn[]
 └── Metadata
```

Adapt field names/types to the existing domain.

Every generated task must have a stable identity within the plan.

Do not rely on random GUIDs for deterministic comparison before persistence.

Use a stable task key such as:

```text
feature.authentication
feature.user-profile
api.auth.login
mobile.login-screen
```

or an equivalent deterministic scheme appropriate to the repository.

The final persisted WorkItems may continue using database GUIDs.

---

# 8. Plan Validation

Implement centralized validation before persistence.

At minimum validate:

### Required fields

- non-empty stable key
- non-empty title
- valid WorkItemType
- valid priority

### Uniqueness

No duplicate task stable keys.

### Dependencies

Every dependency must reference an existing task key.

### Self dependency

Reject:

```text
A → A
```

### Cycles

Reject:

```text
A → B
B → C
C → A
```

Reuse the existing dependency resolver/validation infrastructure where possible.

Do NOT implement a second independent cycle-detection algorithm unless required by a different representation.

### Deterministic ordering

The normalized plan must have a deterministic ordering.

---

# 9. Plan Normalization

Before persistence:

1. normalize task ordering,
2. normalize dependency ordering,
3. normalize enum values,
4. remove exact duplicate dependency declarations if the contract allows them,
5. reject semantic conflicts,
6. calculate a deterministic representation/hash if useful.

The same input must produce the same normalized plan.

Do not use timestamps, random GUIDs, or process-specific state in deterministic planning decisions.

---

# 10. WorkItem Persistence

Persist the plan using the existing WorkItem domain.

Each task should be created in the correct initial state.

Given the existing lifecycle:

```text
Draft → Ready → Running → ...
```

do not bypass `WorkItemTransitions`.

Decide and document whether newly planned tasks should initially be:

```text
Draft
```

or:

```text
Ready
```

based on the existing Project lifecycle and domain semantics.

The important requirement is:

> The Planner must not accidentally dispatch a partially validated task.

A task becomes scheduler-eligible only after the plan has been completely validated and persisted.

---

# 11. Atomic Plan Persistence

Plan creation must not leave a partially persisted DAG.

For a valid plan:

```text
WorkItems
+
Dependencies
```

must be persisted atomically.

If persistence fails:

- no partially-created plan should remain,
- no task should become dispatchable,
- no AgentRun should be created.

Use the existing EF transaction/repository infrastructure.

Do not create a custom transaction manager unless the repository genuinely requires it.

---

# 12. Idempotency

Planning must be safe against repeated requests.

Explicitly define behavior for:

```text
Plan(Project X)
Plan(Project X)
```

Possible acceptable strategies:

- reject if a plan already exists,
- return existing plan,
- create a new explicit plan version.

Choose the strategy that best fits the existing Project model.

Do NOT silently create duplicate WorkItems.

The same task must not be dispatched twice merely because planning was requested twice.

---

# 13. Project Lifecycle Integration

Integrate the Planner with the existing Project lifecycle.

Use the already-established transitions.

The intended lifecycle should remain compatible with:

```text
Created
  → Analyzing
  → Specification
  → Architecture
  → Planning
  → Implementation
  → Testing
  → Reviewing
  → HumanReview
  → Completed
```

Do not invent new Project statuses.

Do not automatically jump through unrelated lifecycle states.

The Planner should have one explicit responsibility:

> Generate and validate the executable WorkItem DAG for a Project.

If automatic `Planning → Implementation` is introduced, only do so if the existing lifecycle and domain semantics clearly justify it; otherwise leave the transition explicit.

---

# 14. AgentDefinition Compatibility

The Phase 18.3 Scheduler currently resolves:

```text
AgentDefinition.AgentType == WorkItem.Type.ToString()
```

before claiming a WorkItem.

The Planner must therefore generate only WorkItemTypes whose execution semantics are already represented by the current system, unless a new type is explicitly required.

Do NOT invent a large catalog of agent types.

If the Planner produces a WorkItem type for which no AgentDefinition exists:

- either reject the plan during validation,
- or explicitly document it as a non-dispatchable planning task.

Prefer early validation when the task is intended for automatic execution.

---

# 15. AgentRun.Model Gap

Phase 18.1.8 identified that:

> `AgentRun.Model` does not yet have end-to-end API wiring.

Do NOT create a broad model-selection subsystem in this phase.

However, inspect whether Planner execution requires model selection.

If it does, introduce only the smallest explicit field/configuration needed to select the Planner model.

Do NOT hard-code model selection into domain entities unless the existing architecture requires it.

Document this decision.

---

# 16. Planning API

If the existing API architecture supports it, add a focused endpoint such as:

```text
POST /api/projects/{projectId}/plan
```

The endpoint should:

- validate request,
- initiate planning,
- return a plan identifier/result,
- not synchronously execute arbitrary WorkItems.

Do NOT add:

```text
POST /api/tasks/{id}/execute
```

or any manual arbitrary execution API.

If planning is asynchronous, follow the existing AgentRun/Worker patterns rather than inventing another background execution mechanism.

---

# 17. Plan Inspection API

If useful and consistent with the existing API:

```text
GET /api/projects/{projectId}/plan
```

should expose:

- task stable key
- title
- type
- priority
- dependencies
- status
- readiness

Do not expose internal persistence details unnecessarily.

Do not create a UI in this phase.

---

# 18. Scheduler Integration

The final output of the Planner must be directly consumable by Phase 18.3.

Verify the complete deterministic chain:

```text
Plan
 ↓
WorkItems
 ↓
Dependencies
 ↓
Readiness
 ↓
TaskDispatchScheduler
 ↓
AgentRun
```

The Planner must not duplicate:

- readiness calculation,
- dependency DFS,
- scheduler ordering,
- AgentRun claiming.

Those remain owned by their existing components.

---

# 19. Tests

Add focused tests for:

### Plan generation

1. valid input produces expected task set.
2. deterministic input produces deterministic task keys.
3. deterministic input produces deterministic dependency graph.
4. priority is preserved.
5. task types are preserved.

### Validation

6. missing stable key rejected.
7. duplicate stable key rejected.
8. missing dependency target rejected.
9. self-dependency rejected.
10. circular dependency rejected.
11. invalid WorkItemType rejected.
12. unsupported/non-dispatchable task rejected where applicable.

### Normalization

13. task ordering is deterministic.
14. dependency ordering is deterministic.
15. equivalent input normalizes identically.

### Persistence

16. valid plan persists all WorkItems.
17. all dependencies persist correctly.
18. persistence is atomic.
19. failed persistence does not leave partial tasks.

### Idempotency

20. repeated planning does not duplicate WorkItems.
21. repeated planning does not create duplicate dependencies.

### Lifecycle

22. Project enters/remaining status follows the documented planning policy.
23. invalid Project state rejects planning.

### Scheduler integration

24. planned Ready WorkItem becomes scheduler-eligible.
25. dependency-blocked task remains blocked.
26. root task can become Ready.
27. scheduler creates AgentRun only for eligible tasks.

Do not call Hermes in all these tests.

Use deterministic fake planner/runtime components.

---

# 20. Real Hermes

Real Hermes execution is optional in this phase.

Do NOT repeat the Phase 18.1.7/18.1.8 stability suite.

If Planner execution is implemented through the real AgentRun/Hermes path, one narrow real-Hermes validation is sufficient:

```text
Project
 → Planner AgentRun
 → Hermes
 → structured planning result
 → validation
 → WorkItems
 → Dependencies
```

Only report it if actually executed.

Never fabricate real LLM evidence.

---

# 21. Migration

First determine whether the current Project/WorkItem/Dependency schema already supports this phase.

Prefer:

```text
No migration
```

if possible.

If a new Plan persistence entity/version is genuinely necessary, create the minimal migration and verify:

```text
dotnet ef migrations has-pending-model-changes
```

before and after.

Do not create schema merely for theoretical future functionality.

---

# 22. Explicit Non-Goals

Do NOT implement:

- Coding Agent
- Developer Agent
- Test Agent
- Reviewer Agent
- Repair Agent
- multi-agent orchestration
- automatic code generation
- Git commit/push
- repository branching
- CI/CD changes
- OpenViking
- learning/experience memory
- autonomous project completion
- UI/dashboard
- new recovery system
- new scheduler
- new dependency engine
- broad model-selection framework
- production deployment changes

This phase ends when a Project has a validated, persisted, deterministic and scheduler-consumable execution plan.

---

# 23. Verification

Run:

```text
dotnet build apps/AI.Factory.slnx -c Debug
dotnet build apps/AI.Factory.slnx -c Release
dotnet test apps/AI.Factory.slnx -c Debug --no-build
dotnet ef migrations has-pending-model-changes
```

Also run the new Planner tests independently.

Verify:

```text
Project
 → Plan
 → WorkItems
 → Dependencies
 → Readiness
 → Scheduler eligibility
```

with an integration test.

Do not claim scheduler integration merely because both components compile.

---

# 24. Required Report

Create:

```text
.ai/context/phase-18.5-project-planner.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Existing Planning/Task Infrastructure
4. Planning Contract
5. Deterministic Planning Rules
6. Validation Rules
7. Normalization
8. Persistence/Transaction Strategy
9. Idempotency Strategy
10. Project Lifecycle Integration
11. Scheduler Integration
12. AgentDefinition Compatibility
13. AgentRun.Model Decision
14. API Changes
15. Tests Added
16. Exact Verification Results
17. Migration Status
18. Real Hermes Result, if actually run
19. Known Limitations
20. Remaining Risks
21. Final Decision

---

# 25. Final Decision

The report must explicitly state:

```text
Project Planner: VERIFIED
```

only if the deterministic planning and persistence pipeline is empirically verified.

Otherwise:

```text
Project Planner: NOT VERIFIED
```

and:

```text
Final Decision: NO-GO
```

The most important proof is:

> Given a valid planning input, the Factory deterministically produces a validated WorkItem DAG, persists it atomically without duplicates, and the existing TaskDispatchScheduler can consume the resulting tasks according to dependency readiness.

---

# 26. Git

Do NOT commit.

Do NOT push.

Do NOT modify remote branches.

Leave all changes uncommitted unless explicitly instructed otherwise.