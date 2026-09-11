# Phase 18.6 — LLM Planner Agent & Candidate Plan Generation

## Phase Objective

Add the first real LLM-backed Planner to AI Factory.

The goal is to close the currently missing loop:

```text
Project
    ↓
Planner Agent
    ↓
LLM-generated candidate plan
    ↓
StructuredAgentResult
    ↓
Candidate Plan extraction
    ↓
ProjectPlanService
    ↓
validation + normalization
    ↓
WorkItems + Dependencies
    ↓
TaskReadinessService
    ↓
TaskDispatchScheduler
    ↓
AgentRun
```

The Planner Agent is an **untrusted candidate-plan generator**.

The Factory remains authoritative for:

- validation
- normalization
- dependency correctness
- cycle detection
- AgentDefinition compatibility
- persistence
- idempotency
- readiness
- scheduling
- execution

The LLM must never directly create WorkItems or dependencies.

---

# 1. Mandatory Repository Inspection

Before changing anything:

1. Read `AGENTS.md`.
2. Read the Phase 18.2 report.
3. Read the Phase 18.3 report.
4. Read the Phase 18.4 report.
5. Read the Phase 18.5 report:
   `.ai/context/phase-18.5-project-planner.md`

Inspect the complete current implementation of:

- `Project`
- `ProjectStatus`
- `ProjectTransitions`
- `WorkItem`
- `WorkItemType`
- `WorkItemStatus`
- `WorkItemDependency`
- `IProjectPlanService`
- `ProjectPlanService`
- `TaskDependencyService`
- `ITaskDependencyResolver`
- `TaskReadinessService`
- `TaskDispatchScheduler`
- `AgentRun`
- `AgentDefinition`
- `AgentRunWorker`
- `AgentOrchestrator`
- `IAgentRuntime`
- `HermesAgentRuntime`
- `IAgentInstructionBuilder`
- `AgentExecutionInstruction`
- `StructuredAgentResult`
- `IStructuredAgentResultParser`
- `AgentRunWorkItemSync`
- existing AgentRun API
- existing AgentDefinition API
- existing model/provider configuration
- existing structured-result tests

Also inspect the legacy:

- `ITaskPlanner`
- `ModelTaskPlanner`
- `TaskPlan`
- `PlannedTask`
- `TaskPlanValidator`
- `OrchestrationRunExecutionEngine`

The legacy planner must be treated as a separate pipeline unless repository inspection proves that an existing component can be safely reused without importing its legacy execution semantics.

Do not modify the legacy pipeline merely to reduce code duplication.

---

# 2. Architectural Principle

The architecture must remain:

```text
                 ┌──────────────────────┐
                 │      Project         │
                 └──────────┬───────────┘
                            │
                            ▼
                 ┌──────────────────────┐
                 │    Planner Agent     │
                 │   LLM / Hermes       │
                 └──────────┬───────────┘
                            │
                            ▼
                 Candidate Plan JSON
                            │
                            ▼
                 ┌──────────────────────┐
                 │ Factory Validation   │
                 │ ProjectPlanService   │
                 └──────────┬───────────┘
                            │
                            ▼
                 WorkItems + Dependencies
                            │
                            ▼
                 TaskDispatchScheduler
```

The Planner Agent is responsible only for producing a candidate.

`ProjectPlanService` remains the authoritative plan boundary.

Never allow the LLM to bypass `ProjectPlanService`.

---

# 3. Do Not Create a Second Planning Domain

Phase 18.5 already established:

```text
ProjectPlanRequest
PlannedWorkItemInput
ProjectPlanResult
WorkItem.PlanKey
```

Reuse these concepts.

Do NOT create:

- `LlmPlan`
- `AiPlan`
- `PlannerWorkItem`
- another task graph
- another dependency model
- another plan persistence table

unless repository inspection proves that the existing contract cannot safely represent the Planner output.

The Planner should ultimately produce the same candidate structure already accepted by:

```text
IProjectPlanService.CreatePlanAsync(...)
```

---

# 4. Planner Agent Definition

Introduce a dedicated Planner Agent concept only if the existing `AgentDefinition` model requires it.

The Planner Agent must be distinguishable from execution agents.

For example:

```text
AgentType = Planner
```

or another repository-consistent value.

Do not invent a large agent catalog.

The Planner is a **Project-level AgentRun**, not a WorkItem execution.

Therefore:

```text
AgentRun.ProjectId = Project.Id
AgentRun.WorkItemId = null
```

unless the current domain provides a better existing representation.

This is intentional.

A Planner Agent does not represent execution of an existing WorkItem.

---

# 5. Planner Execution Must Use Existing Agent Pipeline

The Planner must execute through:

```text
AgentRun
    ↓
AgentRunWorker
    ↓
AgentOrchestrator
    ↓
IAgentRuntime
    ↓
HermesAgentRuntime
    ↓
Hermes
```

Do NOT call:

- Hermes directly
- Docker directly
- `IAgentExecutor` directly
- OpenRouter directly
- OmniRoute directly

from the Planner service.

The Planner must remain runtime-agnostic.

---

# 6. Planner Instruction

Build the Planner's instruction through the existing:

```text
IAgentInstructionBuilder
```

mechanism.

Do not create a second prompt-building framework.

The Planner instruction should clearly tell the LLM:

- it is producing a candidate software execution plan,
- the output is untrusted,
- every task needs a stable `PlanKey`,
- dependencies reference `PlanKey`s,
- no random GUIDs,
- no implementation code,
- no prose outside the structured result,
- only supported `WorkItemType` values,
- only supported priorities,
- dependencies must describe prerequisite relationships,
- the plan should be executable by the existing Factory scheduler.

The instruction should contain enough Project context to produce a useful candidate.

At minimum use the existing:

```text
Project.Name
Project.Description
```

and any already-supported planning context discovered during repository inspection.

Do NOT invent a Specification/Architecture subsystem in this phase.

---

# 7. Candidate Plan Output Contract

The LLM output must use the already-established `StructuredAgentResult`.

Do not introduce another result envelope.

The structured result should contain a planning payload equivalent to:

```json
{
  "version": 1,
  "status": "Succeeded",
  "summary": "...",
  "data": {
    "plan": {
      "version": 1,
      "summary": "...",
      "tasks": [
        {
          "planKey": "api.authentication",
          "title": "Implement authentication API",
          "description": "...",
          "type": "Development",
          "priority": "High",
          "dependsOn": []
        }
      ]
    }
  }
}
```

Adapt the exact JSON shape to the existing `StructuredAgentResult.Data` conventions.

Do not modify the global structured-result contract unless absolutely necessary.

---

# 8. Candidate Extraction

Create a focused application abstraction for converting the structured result into:

```text
ProjectPlanRequest
```

For example:

```text
IPlannerResultMapper
PlannerResultMapper
```

Use a different name if repository conventions suggest a better one.

Its responsibility is:

```text
StructuredAgentResult
        ↓
ProjectPlanRequest
```

It must NOT:

- persist WorkItems,
- validate cycles independently,
- calculate readiness,
- create AgentRuns,
- schedule tasks.

Those responsibilities remain elsewhere.

---

# 9. Untrusted LLM Output

Treat every LLM-generated field as untrusted.

The mapper must reject:

- missing `plan`
- unsupported version
- missing task list
- malformed task objects
- missing PlanKey
- invalid enum strings
- invalid dependency arrays
- unexpected structural types

Do not silently invent missing information.

Do not automatically "repair" malformed LLM output in ways that alter its meaning.

The Factory's deterministic validation remains the final authority.

---

# 10. Planner Orchestration Service

Introduce a focused service responsible for the complete Planner workflow.

Conceptually:

```text
IProjectPlanner
    ↓
Create Planner AgentRun
    ↓
AgentRunWorker executes it
    ↓
wait for terminal result
    ↓
read StructuredAgentResult
    ↓
map candidate
    ↓
IProjectPlanService.CreatePlanAsync
```

However, do NOT build a second background-worker infrastructure.

Reuse the existing AgentRun lifecycle and Worker.

If asynchronous orchestration is needed, use the existing AgentRun model/state rather than creating another execution queue.

---

# 11. Synchronous vs Asynchronous API

Determine the correct behavior from the existing AgentRun API architecture.

Do NOT create an HTTP request that directly executes Hermes synchronously.

The API should initiate Planner execution through the existing AgentRun mechanism.

A suitable pattern is:

```text
POST /api/projects/{projectId}/plan/generate
```

which:

1. validates the Project,
2. creates a Planner AgentRun,
3. returns the AgentRun identifier/state,
4. lets `AgentRunWorker` execute it.

Then a subsequent operation or polling path can obtain the resulting plan.

Reuse existing AgentRun APIs where possible rather than adding redundant endpoints.

Do not add arbitrary task-execution endpoints.

---

# 12. Planner AgentRun Lifecycle

Define the lifecycle clearly.

Expected:

```text
Created
  ↓
Queued
  ↓
Running
  ↓
Succeeded / Failed / Cancelled / TimedOut
```

The Planner AgentRun itself does NOT become a WorkItem.

Therefore:

```text
WorkItemId = null
```

is valid and intentional.

Do not invoke `AgentRunWorkItemSync` for Planner runs.

---

# 13. Planner Result Persistence

Use the existing `AgentRun.StructuredResult` persistence from Phase 18.1.8.

After Planner execution:

```text
AgentRun.StructuredResult
        ↓
PlannerResultMapper
        ↓
ProjectPlanService
```

Do not create a separate planner-result database table.

The existing structured-result persistence is the source of truth for the Planner execution result.

---

# 14. Plan Creation After Planner Execution

The planner execution must NOT directly create database WorkItems while Hermes is running.

Only after the AgentRun reaches a valid terminal result:

```text
StructuredAgentResult.Status == Succeeded
```

should the candidate plan be passed to:

```text
IProjectPlanService.CreatePlanAsync(...)
```

This gives a clean boundary:

```text
LLM output
    ↓
structured result
    ↓
candidate
    ↓
deterministic Factory validation
    ↓
persistence
```

If validation fails:

- Planner AgentRun remains successful because it successfully produced its output,
- plan creation fails deterministically,
- no partial WorkItems are persisted,
- the failure must be observable to the caller.

Do not misrepresent an invalid plan as an LLM execution failure.

---

# 15. Planner Failure Semantics

Distinguish:

### LLM/runtime failure

Examples:

- Hermes failure
- timeout
- cancellation
- unavailable model
- malformed structured result

from:

### Plan validation failure

Examples:

- duplicate PlanKey
- cycle
- missing dependency
- unsupported WorkItemType
- missing AgentDefinition

These are different failure categories.

Do not collapse them into one generic error.

---

# 16. Idempotency

A Project must not accidentally receive multiple Planner runs that later create duplicate plans.

Inspect the existing AgentRun correlation/idempotency mechanisms.

Define a deterministic policy.

At minimum protect against:

```text
Generate Plan
Generate Plan
Generate Plan
```

being concurrently initiated for the same Project.

Acceptable approaches include:

- reject while another Planner AgentRun is active,
- return the existing active Planner AgentRun,
- reuse an existing successful plan,
- rely on the existing ProjectPlanService idempotency boundary where appropriate.

Choose the simplest safe strategy.

Do NOT create a general-purpose distributed locking subsystem.

A concurrent race must not produce duplicate WorkItems.

---

# 17. Existing ProjectPlanService Remains Authoritative

Do not weaken Phase 18.5 validation.

The Planner must continue to pass through:

```text
IProjectPlanService.CreatePlanAsync
```

Therefore all existing rules remain active:

- PlanKey required
- unique PlanKey
- valid WorkItemType
- valid priority
- valid dependency targets
- no self-dependency
- no cycles
- AgentDefinition compatibility
- Project must be in Planning state
- deterministic normalization
- atomic persistence
- idempotency
- plan hash

Do not duplicate these rules inside the Planner.

---

# 18. AgentDefinition for Planner

The Planner Agent itself needs an enabled `AgentDefinition`.

Determine the cleanest way to register/configure it.

Do not automatically seed a large catalog.

If a seed is appropriate, create only the Planner definition.

Do not require a WorkItemType → Planner mapping because Planner is not a WorkItem execution agent.

---

# 19. Model Selection

Phase 18.1.8 explicitly left `AgentRun.Model` without full end-to-end API wiring.

This phase may require selecting a Planner model.

Do NOT build a broad model-selection subsystem.

Instead implement the smallest explicit configuration necessary, for example:

```text
PlannerOptions.Model
```

or an equivalent configuration abstraction consistent with the existing provider/model configuration.

The selected model must flow deterministically into the Planner AgentRun.

Do not hard-code a production model name deep inside Planner business logic.

If the existing `AgentRun.Model` field can already carry the value safely, reuse it.

Document the decision.

---

# 20. Planner Prompt Safety

The Planner prompt must explicitly constrain output.

The LLM must not be allowed to:

- execute tools to modify the repository,
- create files,
- run shell commands,
- commit Git changes,
- push Git changes,
- execute arbitrary WorkItems.

If the existing AgentDefinition/capability system supports capability restrictions, Planner should receive the minimum capabilities required to generate the plan.

Prefer a read-only/no-tool Planner.

Do not add new tool infrastructure unless the current architecture requires it.

---

# 21. Planner Quality vs Determinism

The Planner's **candidate content** may vary because an LLM is nondeterministic.

That is acceptable.

The Factory's **processing of the candidate** must remain deterministic.

Therefore:

```text
LLM candidate
    ↓
Factory validation
    ↓
Factory normalization
    ↓
Factory persistence
```

must be deterministic for a given candidate.

Do NOT attempt to make the LLM itself deterministic unless existing provider configuration already supports it.

The purpose of this phase is not to guarantee identical LLM plans.

---

# 22. End-to-End Planner Flow

Implement and test the following logical flow:

```text
POST generate-plan
        ↓
Project validation
        ↓
Planner AgentRun
        ↓
Queued
        ↓
AgentRunWorker
        ↓
AgentOrchestrator
        ↓
IAgentRuntime
        ↓
Hermes
        ↓
StructuredAgentResult
        ↓
PlannerResultMapper
        ↓
ProjectPlanService
        ↓
WorkItems + Dependencies
        ↓
TaskReadinessService
        ↓
TaskDispatchScheduler
```

The final Planner output must be directly usable by the existing scheduler.

Do not modify the scheduler to accommodate the Planner.

---

# 23. Integration Test Without Real Hermes

Create a deterministic integration test using a fake `IAgentRuntime`.

The fake runtime should return a valid structured planning result.

Prove:

```text
Project
 → Planner AgentRun
 → StructuredAgentResult
 → ProjectPlanService
 → WorkItems
 → Dependencies
 → Readiness
```

Also prove invalid candidate behavior:

```text
Planner AgentRun
 → invalid candidate
 → ProjectPlanService validation failure
 → zero partial WorkItems
```

This test should not depend on external credentials or network connectivity.

---

# 24. Real Hermes E2E

This phase materially adds a new real Planner Agent execution path, so a **single narrow real-Hermes E2E is required if the configured Hermes/OmniRoute infrastructure is available**.

Do not repeat the entire Phase 18.1.7/18.1.8 stability suite.

The real test should prove:

```text
Project
 ↓
Planner AgentRun
 ↓
AgentRunWorker
 ↓
AgentOrchestrator
 ↓
Hermes
 ↓
OmniRoute
 ↓
real LLM
 ↓
StructuredAgentResult
 ↓
ProjectPlanService
 ↓
persisted WorkItems + dependencies
```

Use a tiny test Project with 2–3 tasks.

Require a unique marker in the generated structured result, for example:

```text
AI_FACTORY_PLANNER_E2E_OK
```

Only report the marker if it was actually observed.

If the infrastructure is unavailable:

```text
Real Planner E2E: NOT RUN
```

must be reported honestly.

Do not mark the entire phase VERIFIED solely from fake-runtime tests if the required real-Hermes gate could not be executed.

---

# 25. API

Add only the minimum Planner API required by the existing architecture.

Potential endpoint:

```text
POST /api/projects/{projectId}/plan/generate
```

It should initiate Planner execution rather than execute Hermes inside the HTTP request.

Reuse existing AgentRun response conventions.

If an existing endpoint can naturally support this operation, extend it instead of creating a duplicate.

Do not change:

```text
POST /api/projects/{projectId}/plan
```

semantics from Phase 18.5.

That endpoint already accepts a deterministic candidate plan.

The new operation is conceptually:

```text
generate candidate plan using Planner Agent
```

not:

```text
create arbitrary plan
```

---

# 26. GET / Plan Behavior

Preserve the existing:

```text
GET /api/projects/{projectId}/plan
```

behavior from Phase 18.5.

It must continue to return the persisted deterministic plan.

Do not expose raw LLM output through the plan API.

If Planner execution details are needed, use the existing AgentRun API.

---

# 27. Tests

Add focused tests.

### Planner result mapping

1. valid structured result maps to `ProjectPlanRequest`.
2. missing plan data is rejected.
3. unsupported plan version is rejected.
4. malformed task is rejected.
5. invalid WorkItemType is rejected.
6. invalid priority is rejected.
7. malformed dependency list is rejected.

### Planner execution

8. Planner AgentRun is created with `ProjectId`.
9. Planner AgentRun has no `WorkItemId`.
10. Planner execution uses existing AgentRunWorker path.
11. successful Planner result is persisted as `StructuredAgentResult`.
12. failed Planner AgentRun does not create WorkItems.
13. cancelled Planner AgentRun does not create WorkItems.
14. timed-out Planner AgentRun does not create WorkItems.

### Deterministic plan integration

15. valid candidate reaches `ProjectPlanService`.
16. invalid candidate is rejected by `ProjectPlanService`.
17. invalid candidate leaves zero partial WorkItems.
18. resulting WorkItems and dependencies are persisted correctly.
19. resulting root task is scheduler-eligible.
20. dependent task remains blocked until its dependency completes.

### Idempotency/concurrency

21. repeated generation for the same Project does not create duplicate plans.
22. concurrent generation attempts are handled safely.
23. successful existing plan is not duplicated.

### API

24. generate endpoint creates Planner AgentRun.
25. invalid Project state is rejected.
26. existing plan behavior is preserved.
27. Planner execution failure is represented correctly.
28. successful generation eventually exposes the persisted plan through the existing GET endpoint.

---

# 28. Existing Tests

Run the relevant Phase 18.1–18.5 tests before modifying them.

Do not weaken or rewrite existing assertions.

The legacy:

```text
ModelTaskPlanner
TaskPlanValidator
OrchestrationRunExecutionEngine
AutonomousCodingOrchestrator
```

tests must remain green.

The existing deterministic `ProjectPlanService` tests must remain green.

The existing Scheduler and AgentRun tests must remain green.

---

# 29. Migration

First determine whether the current schema already supports this phase.

Prefer:

```text
No migration
```

if possible.

Planner execution should use the existing:

```text
AgentRun
AgentRun.StructuredResult
AgentRun.ProjectId
AgentDefinition
```

schema.

Do not introduce a `PlannerRun` table or separate plan-result table.

Only create a migration if repository inspection proves it is genuinely necessary.

---

# 30. Explicit Non-Goals

Do NOT implement:

- Specification subsystem
- Architecture subsystem
- automatic requirements analysis
- Coding Agent
- Developer Agent
- Test Agent
- Reviewer Agent
- Repair Agent
- multi-agent orchestration
- autonomous coding
- Git automation
- Git commits
- Git pushes
- CI/CD changes
- OpenViking
- long-term learning
- experience memory
- model-selection framework
- scheduler redesign
- dependency-engine redesign
- recovery redesign
- UI/dashboard
- arbitrary task execution API

This phase is specifically:

> **LLM → Candidate Plan → Deterministic Factory Plan**

---

# 31. Important Boundary

Do not confuse:

```text
Planner Agent
```

with:

```text
Task Planner / legacy ModelTaskPlanner
```

The legacy planner produces planning information for its old single-WorkItem execution pipeline.

The new Planner Agent must produce a persisted WorkItem DAG for:

```text
ProjectPlanService
→ TaskDispatchScheduler
→ AgentRun
```

If code reuse is possible without coupling the two pipelines, reuse small pure components.

Do not merge the pipelines.

---

# 32. Verification

Run:

```text
dotnet build apps/AI.Factory.slnx -c Debug
dotnet build apps/AI.Factory.slnx -c Release
dotnet test apps/AI.Factory.slnx -c Debug --no-build
dotnet ef migrations has-pending-model-changes
```

Also run Planner-specific tests independently.

Verify the full non-Hermes deterministic chain:

```text
Planner AgentRun
 → StructuredAgentResult
 → Candidate Plan
 → ProjectPlanService
 → WorkItems
 → Dependencies
 → Readiness
 → Scheduler eligibility
```

Then, if Hermes/OmniRoute is available, perform the narrow real-Hermes E2E.

---

# 33. Required Report

Create:

```text
.ai/context/phase-18.6-llm-planner-agent.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Legacy Planner vs New Planner Architecture
4. Planner Agent Architecture
5. AgentRun Lifecycle
6. Planner Instruction Contract
7. Structured Result Contract
8. Candidate Mapping
9. Validation Boundary
10. Idempotency/Concurrency Strategy
11. Model Selection Decision
12. API Changes
13. Test Coverage
14. Fake Runtime Integration Result
15. Real Hermes E2E Result
16. Migration Status
17. Exact Verification Results
18. Known Limitations
19. Remaining Risks
20. Final Decision

Clearly distinguish:

```text
LLM execution success
```

from:

```text
candidate plan validation success
```

and from:

```text
plan persistence success
```

---

# 34. Final Decision

The phase may be marked VERIFIED only if:

1. Planner AgentRun uses the existing AgentRun execution pipeline.
2. Structured planning output is persisted through the existing structured-result mechanism.
3. Candidate output is converted into `ProjectPlanRequest`.
4. `ProjectPlanService` remains the deterministic authority.
5. Invalid LLM output cannot create partial WorkItems.
6. A valid candidate produces the expected WorkItem DAG.
7. The resulting DAG is consumable by the existing Scheduler.
8. All regression tests pass.
9. The required real-Hermes E2E is actually executed and passes when the configured infrastructure is available.

Final report must explicitly state:

```text
LLM Planner Agent: VERIFIED
```

or:

```text
LLM Planner Agent: NOT VERIFIED
```

and:

```text
Final Decision: GO
```

or:

```text
Final Decision: NO-GO
```

Never claim real-Hermes verification without actual execution evidence.

---

# 35. Git

Do NOT commit.

Do NOT push.

Do NOT modify remote branches.

Leave all changes uncommitted unless explicitly instructed otherwise.