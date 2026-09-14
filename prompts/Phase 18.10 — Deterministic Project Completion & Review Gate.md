# Phase 18.10 — Deterministic Project Completion & Review Gate

## Phase Objective

Close the next lifecycle gap in the AI Factory:

Phase 18.9 now provides bounded WorkItem repair and independent verification.

The Factory still needs a deterministic **Project-level completion/review gate** that determines when an entire Project is actually ready to leave implementation/testing and become Completed.

The target is:

```text
Project
  ↓
Implementation
  ↓
all required WorkItems complete
  ↓
Project Testing
  ↓
project-level deterministic checks
  ↓
Project Reviewing
  ↓
deterministic review gate
  ↓
HumanReview
  ↓
Completed
```

The critical principle is:

> A Project must never become `Completed` merely because an agent claims completion, because all AgentRuns succeeded, or because an arbitrary number of tasks finished.

Completion must be derived from persisted Factory-owned state and deterministic rules.

---

# 1. Mandatory Repository Inspection

Before modifying anything, inspect:

- `AGENTS.md`
- Phase 18.2 report
- Phase 18.5 report
- Phase 18.6 report
- Phase 18.7 report
- Phase 18.8 report
- Phase 18.9 report
- `Project`
- `ProjectStatus`
- `ProjectTransitions`
- `WorkItem`
- `WorkItemStatus`
- `WorkItemTransitions`
- `WorkItemDependency`
- `TaskDependencyResolver`
- `TaskDispatchScheduler`
- `AgentRun`
- `AgentRunStatus`
- `AgentRunWorker`
- `AgentRunWorkItemSync`
- `IVerificationGate`
- `VerificationGate`
- `RepairAttemptCount`
- existing project APIs
- existing project repositories/services
- existing validation/build/test infrastructure
- existing review-related code, if any
- legacy planning/validation/review infrastructure
- all existing Project lifecycle tests

Do not assume that a new Project orchestration service is required until the existing implementation has been inspected.

---

# 2. Current Lifecycle

The current Project lifecycle already contains:

```text
Created
Analyzing
Specification
Architecture
Planning
Implementation
Testing
Repairing
Reviewing
HumanReview
Completed
Failed
Cancelled
Paused
```

Inspect the exact existing transition table before implementation.

Do not redesign the lifecycle.

Use the smallest changes necessary.

---

# 3. Exact Problem To Solve

WorkItem completion is now independently verified.

However, there is currently no authoritative Project-level mechanism answering:

```text
"Are all required WorkItems complete and verified?"
```

and:

```text
"Is the Project ready for human review?"
```

The new phase must provide that deterministic answer.

---

# 4. Core Project Completion Rule

A Project may enter the Project-level testing/review pipeline only when:

```text
Every required WorkItem is Completed
```

Do NOT consider these states successful:

```text
Ready
Running
Testing
Repairing
Blocked
Failed
Cancelled
```

Only:

```text
Completed
```

satisfies the WorkItem completion requirement.

Reuse the existing `TaskDependencyResolver` semantics where appropriate.

Do not duplicate dependency logic unnecessarily.

---

# 5. Define "Required WorkItem"

Inspect whether the current WorkItem model contains an existing concept that distinguishes required vs optional work.

If such a concept exists, reuse it.

If no such concept exists, treat all WorkItems belonging to the Project as required.

Do NOT add optional-task semantics merely for this phase.

Document the chosen rule.

---

# 6. Project Testing Gate

Introduce the smallest appropriate abstraction, for example:

```text
IProjectCompletionGate
```

or an equivalent existing abstraction if one already exists.

Its responsibility is deterministic Project-level evaluation.

It must NOT execute arbitrary LLM commands.

It must NOT trust AgentRun output.

It must NOT directly mark a Project Completed.

The gate should evaluate facts such as:

- all required WorkItems are Completed
- no WorkItem remains Ready/Running/Testing/Repairing/Blocked
- no WorkItem is Failed/Cancelled
- dependency graph is satisfied
- Project has a valid plan where required by the existing lifecycle
- no active AgentRuns remain for the Project

Only include checks that are actually supported by the existing domain.

Do not invent requirements without repository evidence.

---

# 7. Project Testing State

When Project is in:

```text
Implementation
```

and all required WorkItems reach:

```text
Completed
```

the Factory should deterministically transition the Project toward:

```text
Testing
```

Do not transition to `Completed` directly.

The Project-level Testing state exists specifically to provide an independent gate.

Use existing `ProjectTransitions`.

If the required transition does not exist, add only the minimal legal transition.

---

# 8. Project-Level Verification

Inspect the existing `IVerificationGate`.

Do not blindly reuse the WorkItem gate if its contract is fundamentally WorkItem-specific.

Determine whether Project verification can safely reuse the existing deterministic build/test infrastructure.

Preferred behavior, if supported by the existing model:

```text
Project Testing
      ↓
dotnet build Project.RootPath
      ↓
dotnet test Project.RootPath
      ↓
Passed / Failed / InfrastructureError / TimedOut / Cancelled
```

However:

- do not duplicate `IProcessRunner`
- do not create another process execution abstraction
- do not accept commands from the LLM
- do not use Project.Description as a command
- use Factory-owned `Project.RootPath`

If the existing WorkItem `IVerificationGate` can be generalized cleanly without breaking its contract, prefer that over creating duplicate process logic.

Otherwise create the smallest Project-specific adapter around the existing build/test executors.

---

# 9. Project Testing Failure Policy

If Project-level verification fails because of a genuine implementation/build/test failure:

```text
Project Testing
      ↓
Project Repairing
```

only if the existing Project lifecycle supports such a state.

If there is no Project-level `Repairing` state, do NOT invent a second project repair mechanism.

Instead determine whether the correct deterministic behavior is:

```text
Project Testing -> Failed
```

or:

```text
Project Testing -> Implementation
```

based strictly on the existing lifecycle and WorkItem repair architecture.

The preferred architecture is to repair at the WorkItem level rather than create a second competing Project repair loop.

Therefore:

> Project-level verification failure should identify that WorkItems need attention, but this phase must not create a new Project-wide repair agent loop.

Document the exact chosen behavior.

---

# 10. Project Reviewing Gate

After Project-level testing passes:

```text
Testing -> Reviewing
```

The Reviewing state must have an explicit deterministic purpose.

Do not simply transition through Reviewing because the enum exists.

Define a minimal review gate based on existing persisted information.

At minimum, inspect:

- all WorkItems Completed
- latest verification state available through persisted WorkItem state
- no active AgentRuns
- plan exists where the current lifecycle requires one
- no failed/cancelled WorkItems

Do not claim that code quality, architecture quality, security, UX, etc. has been reviewed unless an actual mechanism exists.

---

# 11. HumanReview Boundary

The Project must reach:

```text
HumanReview
```

before final completion unless the existing lifecycle explicitly defines another valid route.

The system must NOT automatically mark:

```text
Reviewing -> Completed
```

if `HumanReview` is the established lifecycle boundary.

The goal of this phase is to make:

```text
Reviewing -> HumanReview
```

deterministic.

Human approval itself remains an external/human decision unless an existing explicit approval mechanism already exists.

---

# 12. Project Completed Rule

The only valid path to:

```text
ProjectStatus.Completed
```

must be an explicitly authorized lifecycle transition from the existing HumanReview flow.

Do not let:

- AgentRun success
- WorkItem completion
- LLM output
- planner output
- scheduler completion
- verification success alone

directly mark Project as Completed.

Completion must respect the domain transition table.

---

# 13. Idempotency / Concurrency

Project completion evaluation may be triggered multiple times.

The implementation must be safe under:

- duplicate Worker observations
- multiple scheduler cycles
- concurrent WorkItem completions
- repeated Project gate evaluation
- process restart

Use existing optimistic concurrency/version handling.

Do NOT introduce:

- static locks
- distributed locks
- singleton state
- in-memory ownership maps

If another writer already transitioned the Project, the losing evaluation must safely return.

---

# 14. No Premature Project Transition

The following must never cause Project completion:

```text
One WorkItem completed
Several WorkItems completed
All AgentRuns succeeded
Planner Agent succeeded
Repair Agent succeeded
VerificationGate passed for one WorkItem
```

The Project gate must evaluate the complete persisted Project state.

---

# 15. Empty Project Policy

Determine explicitly what should happen when a Project has:

```text
zero WorkItems
```

Do not silently treat an empty Project as completed unless the existing domain clearly establishes that semantics.

Prefer fail-closed behavior if the current lifecycle does not define empty-project completion.

Add a deterministic test for the chosen policy.

---

# 16. Active AgentRun Policy

Determine how to identify active Project AgentRuns.

At minimum inspect:

```text
Created
Queued
Running
Waiting
```

and any other non-terminal statuses defined by `AgentRunStatus`.

A Project must not be considered ready for final review while an active Project-level AgentRun is still executing, unless the existing lifecycle explicitly allows that.

Do not confuse historical successful AgentRuns with active execution.

---

# 17. Planner Agent Interaction

Phase 18.6 introduced the Project Planner Agent.

Do not create a second planning mechanism.

The Project gate must operate only on persisted plan/work-item state.

A successful Planner AgentRun is not itself proof that a valid executable Project exists.

The persisted `ProjectPlanService` result and WorkItem DAG remain authoritative.

---

# 18. Scheduler Interaction

Do not create a second scheduler.

The existing `TaskDispatchScheduler` remains responsible for WorkItem dispatch.

The Project completion/review gate should observe scheduler/domain state rather than replacing the scheduler.

If the current scheduler has no mechanism to notify Project lifecycle progression, add the smallest deterministic integration point.

Do not introduce event-bus infrastructure unless the repository already has it and it is necessary.

---

# 19. Recovery

Inspect existing:

```text
WorkflowRecoveryHostedService
```

and Worker behavior.

Do not create a new recovery framework.

Project lifecycle evaluation must remain safe after process restart because the authoritative inputs are persisted state.

If a Project is left in Testing or Reviewing after restart, determine whether the existing Worker can safely re-evaluate it.

If a recovery gap exists, document it rather than introducing unrelated recovery architecture.

---

# 20. API

Inspect the current Project API.

If Project status is already exposed, no new endpoint is required.

If there is an existing endpoint for Project lifecycle transitions, reuse it.

Do not create an endpoint solely for internal automatic progression.

If HumanReview requires an explicit API transition and one already exists, verify it.

If it does not exist and the current architecture requires an explicit human action, add the smallest appropriate endpoint.

Do not expose internal gate implementation details unnecessarily.

---

# 21. Tests — Mandatory

Add deterministic tests covering:

### Completion detection

1. `ProjectWithAllWorkItemsCompleted_IsEligibleForTesting`
2. `ProjectWithIncompleteWorkItem_IsNotEligibleForTesting`
3. `ProjectWithRunningWorkItem_IsNotEligible`
4. `ProjectWithTestingWorkItem_IsNotEligible`
5. `ProjectWithRepairingWorkItem_IsNotEligible`
6. `ProjectWithBlockedWorkItem_IsNotEligible`
7. `ProjectWithFailedWorkItem_IsNotEligible`
8. `ProjectWithCancelledWorkItem_IsNotEligible`

### Dependency semantics

9. `CompletedDependency_AllowsProjectCompletionEvaluation`
10. `UncompletedDependency_PreventsProjectCompletionEvaluation`

### Active AgentRuns

11. `ActiveProjectAgentRun_PreventsReview`
12. `TerminalProjectAgentRuns_DoNotBlockReview`

### Lifecycle

13. `AllWorkItemsCompleted_TransitionsProjectToTesting`
14. `ProjectTestingPassed_TransitionsToReviewing`
15. `ProjectReviewing_TransitionsToHumanReview`
16. `ProjectCannotSkipHumanReview`
17. `ProjectCannotBecomeCompletedFromAgentSuccess`

### Verification

18. `ProjectVerificationBuildFailure_IsNotPassed`
19. `ProjectVerificationTestFailure_IsNotPassed`
20. `ProjectVerificationTimeout_IsNotPassed`
21. `ProjectVerificationCancellation_IsNotPassed`
22. `ProjectVerificationSuccess_AllowsReview`

### Idempotency / concurrency

23. `RepeatedProjectGateEvaluation_IsIdempotent`
24. `ConcurrentProjectGateEvaluation_IsConcurrencySafe`
25. `AlreadyTerminalProject_IsNotOverwritten`

### Empty project

26. `EmptyProject_UsesExplicitFailClosedPolicy`

### End-to-end

27. `EndToEnd_AllWorkItemsComplete_ProjectTests_Reviews_AndReachesHumanReview`

Use real SQLite for persistence/concurrency integration tests where appropriate.

Use fake executors for deterministic project-gate tests.

Use real build/test only where it materially proves integration and does not make the full suite unnecessarily slow.

---

# 22. End-to-End Scenario

At minimum prove this persisted lifecycle:

```text
Project
  ↓
Implementation
  ↓
WorkItem A Completed
WorkItem B Completed
WorkItem C Completed
  ↓
Project Testing
  ↓
deterministic Project verification
  ↓
Passed
  ↓
Project Reviewing
  ↓
Review gate passed
  ↓
HumanReview
```

The test must explicitly prove that the Project does NOT become Completed automatically.

If the existing human-review API/transition is appropriate, separately test:

```text
HumanReview -> Completed
```

using the existing authorized mechanism.

---

# 23. Real Hermes

Do not make real Hermes execution a prerequisite for this phase.

This phase is fundamentally a deterministic Project-domain gate.

If an opt-in real Hermes test is added, use the existing:

```text
AI_FACTORY_HERMES_SMOKE_TEST
```

mechanism.

Do not claim real Hermes evidence unless it was actually executed.

---

# 24. Legacy Infrastructure

Inspect the legacy:

- `ValidationEngine`
- `ValidationRun`
- `AutonomousCodingOrchestrator`
- legacy planning pipeline
- any review/approval infrastructure

Do not merge old and new pipelines merely because they have similar names.

If legacy infrastructure contains reusable low-level components, reuse them carefully.

Do not make the new Project lifecycle dependent on the old autonomous orchestration pipeline.

---

# 25. Migration Policy

Prefer no migration.

Only introduce schema changes if a deterministic Project gate genuinely requires persisted state that cannot be derived from existing fields.

Do not persist ephemeral verification output merely for convenience.

If persistence is required, use the smallest additive schema change and validate migrations.

---

# 26. Validation Gates

Run targeted Project lifecycle tests first.

Then run:

```text
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

If schema changed:

```text
dotnet ef migrations has-pending-model-changes \
  --project ../AI.Factory.Infrastructure \
  --startup-project .
```

Also run the relevant existing WorkItem/AgentRun/Scheduler tests to ensure no regression.

Do not report fabricated results.

---

# 27. Empirical Acceptance Criteria

GO only if all are demonstrated:

- Project completion eligibility is derived from persisted WorkItem state.
- Only Completed WorkItems satisfy the completion requirement.
- Running/Testing/Repairing/Blocked/Failed/Cancelled prevent Project review.
- Active AgentRuns are handled deterministically.
- Project does not skip Testing.
- Project does not skip Reviewing.
- Project does not skip HumanReview.
- Project cannot become Completed merely from AgentRun success.
- Project verification uses Factory-owned commands only.
- Project verification cannot be controlled by LLM output.
- Project gate is idempotent.
- Project gate is concurrency-safe.
- Empty Project behavior is explicit and tested.
- Existing WorkItem scheduler behavior remains intact.
- Existing repair loop remains intact.
- Full regression suite passes.
- Debug and Release builds pass.
- Migration state is clean if applicable.

---

# 28. Required Final Report

Create:

```text
.ai/context/phase-18.10-project-completion-review-gate.md
```

Include:

1. Executive Summary
2. Repository Discovery
3. Existing Infrastructure Reused
4. Exact Gap Before Phase 18.10
5. Project Completion Eligibility Rules
6. Project Testing Gate
7. Project Verification
8. Project Reviewing Gate
9. HumanReview Boundary
10. Completion Policy
11. Active AgentRun Policy
12. Dependency Semantics
13. Concurrency / Idempotency
14. Recovery Behavior
15. API Changes
16. Tests Added
17. Exact Build/Test Results
18. Migration Status
19. Real Hermes E2E Result
20. Known Limitations
21. Remaining Risks
22. Final Decision

The final section MUST contain exactly one of:

```text
Project Completion & Review Gate: VERIFIED
Final Decision: GO
```

or:

```text
Project Completion & Review Gate: NOT VERIFIED
Final Decision: NO-GO
```

Do not declare GO without empirical evidence.

---

# Definition of Done

Phase 18.10 is complete when the Factory can deterministically recognize:

```text
all required WorkItems
        ↓
Completed
        ↓
Project Testing
        ↓
independent Project verification
        ↓
Project Reviewing
        ↓
HumanReview
```

without allowing any LLM or AgentRun to falsely declare the Project complete.

The resulting architecture must establish a clear boundary:

```text
Agents produce implementation.
Verification proves implementation.
Project gates prove project readiness.
HumanReview authorizes final completion.
```