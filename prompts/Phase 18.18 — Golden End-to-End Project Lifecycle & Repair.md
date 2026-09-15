# Phase 18.18 — Golden End-to-End Project Lifecycle & Repair

## Phase Objective

Prove the complete AI Factory product flow as one reproducible, end-to-end Golden Path.

Phase 18.17 established:

- unified Project workspace resolution;
- Context/Agent/Verification using the same workspace authority;
- Git-initialized Project source;
- real Docker/Hermes WorkItem E2E infrastructure;
- real host-side workspace modification verification.

The next gap is not another isolated subsystem.

The goal now is to prove that the **entire Project lifecycle works from beginning to end**:

```text
Create Project
    ->
Provision Workspace
    ->
Initialize Git Source
    ->
Analyze / Plan
    ->
Create WorkItems + Dependencies
    ->
Dispatch AgentRuns
    ->
Real Hermes
    ->
Real source modification
    ->
Factory Verification
    ->
Repair when verification fails
    ->
Re-execution
    ->
All WorkItems Completed
    ->
Project Testing
    ->
Project Reviewing
    ->
HumanReview
    ->
Explicit Human Approval
    ->
Project.Completed
```

This phase is the first true **product-level acceptance phase**.

Do not redesign the architecture already established in Phases 18.2–18.17.

---

# 1. Repository Inspection

Before coding, inspect:

- `AGENTS.md`
- Phase 18.15
- Phase 18.16
- Phase 18.17
- `ProjectService`
- `ProjectWorkspaceProvisioner`
- `ProjectSourceInitializer`
- `IWorkspaceResolver`
- `LocalContextProvider`
- `ProjectPlanner`
- `ProjectPlanService`
- `TaskDispatchScheduler`
- `AgentRunWorker`
- `AgentOrchestrator`
- `HermesAgentRuntime`
- `HermesAgentExecutor`
- `StructuredAgentResult`
- `AgentRunWorkItemSync`
- `WorkItemCompletionProcessor`
- `IVerificationGate`
- `RepairAttemptCount`
- `ProjectCompletionGate`
- `ProjectService.ApproveAsync`
- `TaskSchedulerHostedService`
- existing real Hermes E2E tests
- existing Planner E2E tests
- Project lifecycle API
- WorkItem API
- AgentDefinition configuration/seeding
- current audit/logging/observability

Determine exactly which parts already exist and which are still only tested independently.

Do not duplicate existing functionality.

---

# 2. Golden Project Fixture

Create one canonical small project fixture specifically for end-to-end acceptance testing.

Requirements:

- real `.NET` solution/project;
- deterministic;
- tiny;
- fast build/test;
- no external package/service dependency unless absolutely required;
- source initialized through the actual Phase 18.16 source mechanism;
- local Git repository fixture preferred;
- tests must be repeatable from a clean temporary environment.

The fixture should contain a small implementation that an Agent can modify safely.

Example concept:

```text
src/
    Demo.csproj
    GreetingService.cs

tests/
    Demo.Tests.csproj
    GreetingServiceTests.cs
```

Do not require the coding agent to invent the complete application from an empty directory.

This phase is about proving the factory pipeline, not testing arbitrary project generation quality.

---

# 3. Golden Scenario Design

Create one deterministic scenario with multiple WorkItems.

At minimum:

```text
T01 -> baseline/source setup
T02 -> implementation change
T03 -> test/change
T04 -> final integration
```

Use real dependencies:

```text
T02 depends on T01
T03 depends on T02
T04 depends on T03
```

The exact tasks must be chosen so that the resulting project can be deterministically verified.

Do not generate the Golden Scenario dynamically from an unconstrained LLM plan unless an existing Planner path can produce a stable equivalent.

Prefer a known deterministic plan for the first acceptance test.

Planner E2E may be added as a separate assertion after the deterministic lifecycle has been proven.

---

# 4. AgentDefinition Readiness

Before execution, verify that all AgentDefinitions required by the Golden Scenario actually exist.

At minimum identify:

```text
Planner
Implementation
Repair
```

where applicable.

The test must not silently depend on manually pre-created records that are not documented.

If there is still no formal AgentDefinition bootstrap/seeding mechanism, create the smallest deterministic test/operational bootstrap required for the Golden Scenario.

Do not redesign AgentDefinition architecture.

The outcome must make it possible for another developer to reproduce the E2E without guessing which database rows must exist.

---

# 5. Deterministic Project Creation

The Golden Path must start with:

```text
ProjectService.CreateAsync
```

or the actual public API used by normal users.

Do not manually seed:

- Project.RootPath;
- Project.Status;
- workspace directory.

The test must prove the normal creation flow creates a valid provisioned Project.

---

# 6. Source Initialization

Use the actual Phase 18.16 source initialization path.

Preferred:

```text
Project creation
    ->
ProjectSourceInitializer
    ->
local Git repository
    ->
Project.RootPath
```

Verify:

- source files exist;
- correct branch/revision exists;
- Project source metadata is persisted;
- no scratch copy exists between source and execution workspace.

Do not bypass the source API for the primary E2E.

---

# 7. Planner Path

If the project is intended to use Planner automatically, test:

```text
Project
    ->
Planner AgentRun
    ->
Hermes / LLM
    ->
StructuredAgentResult
    ->
ProjectPlanService
    ->
WorkItems
```

Use the existing Planner pipeline.

If real LLM Planner output is not deterministic enough for the Golden acceptance test, separate:

```text
A. deterministic lifecycle acceptance
B. real Planner E2E
```

rather than making the entire acceptance test flaky.

The Golden Path must remain reproducible.

---

# 8. WorkItem Dispatch

Use the real:

```text
TaskDispatchScheduler
```

and:

```text
AgentRunWorker
```

Do not directly invoke `AgentOrchestrator` for the primary Golden Path.

The objective is to prove the actual production orchestration.

Verify:

- Ready WorkItems become Running;
- dependencies are respected;
- only eligible WorkItems are dispatched;
- AgentRuns contain correct ProjectId/WorkItemId;
- scheduler concurrency behavior remains as configured.

Keep:

```text
MaxConcurrentTasks = 1
```

for this Golden Scenario.

---

# 9. Real Hermes Execution

The primary acceptance path must use the real Hermes infrastructure from Phase 18.17 where the environment supports it.

Do not use `FakeAgentRuntime` for the primary Golden Path.

The real chain must be:

```text
TaskDispatchScheduler
    ->
AgentRun
    ->
AgentRunWorker
    ->
AgentOrchestrator
    ->
HermesAgentRuntime
    ->
HermesAgentExecutor
    ->
Docker
    ->
real Hermes
```

The report must explicitly record whether this actually ran.

Do not write:

```text
Real Hermes E2E implemented
```

and treat that as evidence.

The report must say:

```text
REAL HERMES: PASSED
```

or:

```text
REAL HERMES: NOT RUN
```

or:

```text
REAL HERMES: FAILED
```

with the reason.

---

# 10. Prove Real Source Modification

The Golden Path must independently inspect the host Project workspace after Hermes returns.

Do not trust:

```text
StructuredAgentResult.execution.status = completed
```

The test must prove that the expected source file actually changed.

For example:

```text
BEFORE
"Hello"

AFTER
"Hello, AI Factory!"
```

The exact change should be deterministic and independently checkable.

---

# 11. Factory Verification

After Agent execution:

```text
Agent result
    ->
AgentRunWorkItemSync
    ->
VerificationGate
```

The build/test must run against the same Project workspace.

Prove:

```text
Agent workspace
==
Project.RootPath
==
Verification workspace
```

Do not merely compare path strings.

Verify the modified file is the file actually compiled/tested.

---

# 12. Mandatory Repair Scenario

The Golden Path must include at least one deliberate verification failure.

Do not simulate Repair purely by setting a WorkItem status.

Create a controlled scenario where:

```text
Agent execution
    ->
source is intentionally incorrect
    ->
dotnet test fails
```

Then prove:

```text
Verification failed
      ->
WorkItem.Repairing
      ->
Repair AgentRun
      ->
Hermes modifies source
      ->
Testing
      ->
Verification passed
      ->
WorkItem.Completed
```

Use the existing:

```text
RepairAttemptCount
MaxRepairAttempts
```

mechanism.

Do not introduce another retry counter.

---

# 13. Repair Must Be Real

When environment permits, the Repair step must use:

```text
real Hermes
```

not a FakeAgentRuntime.

If making the Repair E2E real is technically too expensive for the initial test, first implement a deterministic fake-runtime regression and separately attempt the real Repair test.

Do not claim the Repair loop is real merely because a fake Agent caused the state transition.

---

# 14. Review / Evaluation / Learning

After successful verification, the existing:

```text
WorkItemCompletionProcessor
```

must run its current review/evaluation/learning responsibilities.

Do not reimplement them in the test.

Verify the expected current behavior:

```text
Verification passed
    ->
Code Review
    ->
Evaluation
    ->
Learning/Lesson recording
    ->
Completed
```

Where the current implementation intentionally swallows an evaluation failure, preserve that behavior and verify it is observable/logged rather than changing it in this phase.

---

# 15. All WorkItems Completed

After all required WorkItems reach:

```text
Completed
```

verify that the Project-level gate reacts automatically.

Expected:

```text
Implementation
    ->
Testing
    ->
Reviewing
    ->
HumanReview
```

Use the existing:

```text
TaskSchedulerHostedService
ProjectCompletionGate
```

path.

Do not manually transition these states in the Golden Path except for the final human approval.

---

# 16. Project-Level Verification

Verify the Project-level verification actually runs against:

```text
Project.RootPath
```

and that all source modifications from WorkItems are present.

Do not substitute WorkItem-level verification for Project-level verification.

The Golden Path must prove both layers:

```text
WorkItem Verification
+
Project Verification
```

---

# 17. HumanReview

Verify:

```text
Project.Reviewing
    ->
Project.HumanReview
```

occurs automatically through the existing ProjectCompletionGate.

Do not call `ProjectService.TransitionStatusAsync` to produce HumanReview in the Golden Path.

---

# 18. Human Approval

Only the explicit approval operation may perform:

```text
HumanReview
    ->
Completed
```

Use:

```http
POST /api/projects/{id}/approve
```

or the actual Phase 18.11 endpoint.

The E2E must prove that the generic status endpoint cannot bypass the boundary.

Final assertion:

```text
Project.Status == Completed
```

---

# 19. Complete Golden Path Assertion

At the end, the test/report must prove the entire sequence:

```text
Project Created
        ✅
Workspace Provisioned
        ✅
Git Source Initialized
        ✅
WorkItems Created
        ✅
Dependencies Respected
        ✅
AgentRun Created
        ✅
Real Hermes Executed
        ✅
Real Workspace Modified
        ✅
Factory Verification Passed
        ✅
At Least One Repair
        ✅
Repair Verification Passed
        ✅
All WorkItems Completed
        ✅
Project Testing
        ✅
Project Reviewing
        ✅
HumanReview
        ✅
Human Approval
        ✅
Project Completed
        ✅
```

---

# 20. Evidence Collection

The Golden E2E should collect enough evidence to diagnose failure.

At minimum capture:

```text
ProjectId
Project.RootPath
source revision
WorkItem IDs
AgentRun IDs
AgentRun statuses
WorkItem statuses
repair attempts
verification results
Project statuses
final approval result
```

Do not store secrets in the evidence.

Do not log API keys or provider credentials.

---

# 21. Manual Golden Path

In addition to automated tests, document a manual run that a developer can execute.

It must use actual implemented APIs/commands only.

The manual sequence should be:

```text
1. Start Docker
2. Start Hermes/Provider infrastructure
3. Start API
4. Start Worker
5. Create Project
6. Initialize Git source
7. Confirm workspace
8. Create Plan / WorkItems
9. Start scheduler
10. Watch AgentRuns
11. Confirm Hermes container
12. Confirm source modification
13. Observe Verification
14. Observe Repair
15. Observe WorkItem.Completed
16. Observe Project Testing
17. Observe Reviewing
18. Observe HumanReview
19. Approve Project
20. Confirm Project.Completed
```

Record the actual API paths and request payloads from the current repository.

Do not invent commands.

---

# 22. Failure Diagnostics

The Golden test must make it possible to distinguish:

```text
Project creation
Workspace provisioning
Git source initialization
Planner
AgentDefinition
Task dispatch
AgentRun
Docker
Hermes
LLM/provider
workspace mount
source modification
Structured result
WorkItem verification
Repair
Project verification
HumanReview
Approval
```

Do not report all failures as "E2E failed".

---

# 23. Existing Test Suite

Do not duplicate all previous unit-test matrices.

Run focused Golden Path tests first.

Then run the existing complete deterministic suite.

Do not delete existing tests merely because the Golden Path covers them.

The Golden Path is acceptance evidence, not a replacement for unit tests.

---

# 24. Real Hermes Environment

The report must explicitly state the environment used.

At minimum:

```text
Docker: available/unavailable
Hermes: available/unavailable
Provider/OmniRoute: available/unavailable
AI_FACTORY_HERMES_SMOKE_TEST: enabled/disabled
```

Never fabricate a live result.

---

# 25. Migration

Prefer zero migration.

This phase should use existing schema.

If a migration becomes necessary, stop and explain exactly why it is required before proceeding with unrelated schema changes.

---

# 26. No Scope Creep

Do not:

- redesign Project lifecycle;
- redesign WorkItem state transitions;
- redesign AgentRun;
- redesign Scheduler;
- add new workspace authorities;
- add private Git authentication;
- add GitHub/GitLab/ADO provider integrations;
- add true AgentRun cancellation/preemption;
- replace Hermes;
- redesign Planner;
- increase same-project concurrency;
- redesign EvaluationEngine;
- redesign LearningService;
- remove `HermesExecution` cleanup tables;
- clean up orphan Trace services unless required to make this E2E observable.

This phase is about **acceptance proof of the complete existing architecture**.

---

# 27. Validation

Run deterministic focused tests first.

Then:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Run migration validation:

```bash
dotnet ef migrations has-pending-model-changes \
  --startup-project ../AI.Factory.Api \
  --project .
```

Then run the real E2E in an environment with Docker and provider access using the repository's actual enablement mechanism.

Use the exact test filter discovered from the repository.

The report must clearly separate:

```text
Deterministic tests
vs.
Real Hermes E2E
```

---

# 28. GO / NO-GO Criteria

### GO requires:

- complete Project lifecycle executes without manual state manipulation except final approval;
- real source exists in Project.RootPath;
- Scheduler creates the AgentRun;
- real Hermes executes at least one WorkItem when live infrastructure is available;
- host Project.RootPath contains the actual Agent modification;
- Factory Verification validates that same workspace;
- Repair path is proven deterministically;
- all WorkItems can reach Completed through the existing pipeline;
- Project reaches HumanReview automatically;
- only explicit approval produces Completed;
- no new execution architecture is introduced.

### NO-GO if:

- real Hermes is claimed without actually running it;
- Agent result is treated as sufficient without Factory verification;
- Repair is simulated only by manually changing status;
- Project lifecycle requires manual transitions before HumanReview;
- generic status mutation can bypass the approval boundary;
- Agent and Verification operate on different source trees;
- the Golden Path depends on undocumented database state;
- any production component bypasses the unified workspace authority;
- failure diagnostics are insufficient to identify which stage failed.

---

# 29. Final Report

Create:

```text
.ai/context/phase-18.18-golden-end-to-end-project-lifecycle.md
```

The report must contain:

1. Golden Project definition.
2. Source fixture details.
3. WorkItem graph.
4. AgentDefinitions required.
5. Exact lifecycle execution.
6. Real Hermes environment.
7. Real Hermes result.
8. Actual modified source file.
9. Verification result.
10. Repair result.
11. Review/evaluation/learning result.
12. Project gate result.
13. HumanReview result.
14. Approval result.
15. Final Project status.
16. All IDs needed to trace the execution.
17. Deterministic test results.
18. Full build/test results.
19. Migration status.
20. Remaining blockers for production.

---

# 30. Definition of Done

The ultimate proof is one reproducible scenario:

```text
             REAL PROJECT
                  |
                  v
           REAL GIT SOURCE
                  |
                  v
        PROVISIONED WORKSPACE
                  |
                  v
              PLANNING
                  |
                  v
             WORKITEMS
                  |
                  v
         TASK DISPATCH SCHEDULER
                  |
                  v
               AGENTRUN
                  |
                  v
            REAL HERMES
                  |
                  v
       REAL SOURCE MODIFICATION
                  |
                  v
          FACTORY VERIFICATION
                  |
            +-----+-----+
            |           |
          PASS         FAIL
            |           |
            |           v
            |        REPAIR
            |           |
            |           v
            |       RE-EXECUTE
            |           |
            +-----+-----+
                  |
                  v
          ALL WORKITEMS COMPLETE
                  |
                  v
          PROJECT TESTING
                  |
                  v
          PROJECT REVIEWING
                  |
                  v
            HUMAN REVIEW
                  |
                  | explicit approval
                  v
          PROJECT COMPLETED
```

The report must explicitly distinguish:

```text
ARCHITECTURE VERIFIED
```

from:

```text
REAL PRODUCT E2E VERIFIED
```

Both are required for a full GO.

Final decision must be exactly:

```text
Golden Project Lifecycle: VERIFIED
Final Decision: GO
```

or:

```text
Golden Project Lifecycle: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> Unit tests prove components.
> Integration tests prove boundaries.
> Real Hermes proves execution.
> The Golden Project proves the product.
> Factory verification proves the code actually works.
> Repair proves failure recovery.
> Project gates prove project readiness.
> Human approval proves final completion authority.