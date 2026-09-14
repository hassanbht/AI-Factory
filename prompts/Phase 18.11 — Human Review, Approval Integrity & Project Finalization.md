# Phase 18.11 — Human Review, Approval Integrity & Project Finalization

## Phase Objective

Close the final lifecycle boundary left after Phase 18.10.

Phase 18.10 established the deterministic path:

`Implementation -> Testing -> Reviewing -> HumanReview`

and explicitly prevented the automatic completion gate from producing `ProjectStatus.Completed`.

The remaining integrity gap is that the existing manual project-status API still permits a direct:

`Reviewing -> Completed`

transition, bypassing `HumanReview`.

This phase must make **HumanReview the authoritative approval boundary** for the new Project lifecycle and ensure that only an explicit, auditable human approval action can move a Project from `HumanReview` to `Completed`.

This phase is about lifecycle integrity and human authorization only.

Do not rebuild any of the WorkItem execution, verification, repair, scheduler, or project completion logic already implemented in Phases 18.3–18.10.

---

## 1. Repository Inspection

Before changing code, inspect the repository and existing implementation.

Read:

- `AGENTS.md`
- Phase 18.10 report
- `Project`, `ProjectStatus`, `ProjectTransitions`
- `ProjectService`
- existing Project status API endpoints
- `ProjectsAndTasksApiTests`
- any authorization/current-user abstractions already present
- any existing approval / review infrastructure
- `ApprovalRequest`, `ApprovalService`
- `IWorkflowEngine`, `WorkflowEngine`, `WorkflowStateStore`, `WorkflowTransitionValidator`
- legacy `AutonomousCodingOrchestrator`
- existing persistence conventions for audit/history/current actor information
- all existing Project lifecycle tests

Determine first whether the repository already has an appropriate abstraction for:

- authenticated actor identity
- authorization / roles
- human approval
- audit events
- review/approval records

Reuse existing infrastructure where it actually fits.

Do not introduce a second approval framework if one already exists and can be safely adapted.

---

## 2. Primary Goal — Protect the HumanReview Boundary

Establish the following invariant for the new Phase 18.3+ Project lifecycle:

```text
Implementation
    -> Testing
    -> Reviewing
    -> HumanReview
    -> Completed
```

The automatic ProjectCompletionGate must remain unchanged in principle:

```text
Reviewing -> HumanReview
```

and never:

```text
Reviewing -> Completed
```

The key change in this phase is that the generic/manual Project status transition mechanism must no longer allow a caller to bypass the HumanReview boundary.

Specifically:

```text
Reviewing -> Completed
```

must no longer be an allowed generic/manual transition.

`HumanReview -> Completed` must remain the only completion transition for the new Project lifecycle.

---

## 3. Preserve Existing Domain Safety

Do not weaken `ProjectTransitions`.

Prefer to make the domain transition table reflect the actual lifecycle rule rather than merely adding controller-level restrictions.

The desired semantic model is:

```text
Reviewing -> HumanReview
HumanReview -> Completed
```

and not:

```text
Reviewing -> Completed
```

unless there is a clearly identified legacy-only requirement that makes such a change unsafe.

If legacy code still depends on `Reviewing -> Completed`, identify that dependency explicitly and isolate it instead of silently preserving a bypass in the new pipeline.

Do not allow the same Project to have two contradictory completion semantics.

---

## 4. Explicit Human Approval Operation

Introduce the smallest appropriate explicit operation for approval.

Preferred shape:

```text
ApproveProjectAsync(projectId, actor/context, cancellationToken)
```

or an equivalent existing approval abstraction discovered during repository inspection.

The operation must:

1. Load the Project.
2. Verify the Project is currently `HumanReview`.
3. Perform the transition through the existing domain transition mechanism.
4. Persist safely using the existing optimistic concurrency/version mechanism.
5. Be idempotent with respect to repeated requests.
6. Produce a clear conflict/error for approval attempted from any other lifecycle state.
7. Never accept agent-produced approval.
8. Never use `AgentRun.StructuredResult` as authorization for completion.

Do not introduce an LLM-driven approval path.

---

## 5. API Boundary

Inspect the existing:

```text
PATCH /api/projects/{id}/status
```

and determine whether it should remain a general administrative status-transition endpoint.

The preferred outcome for the new lifecycle is:

- generic status mutation cannot bypass `HumanReview`;
- explicit approval uses a dedicated semantic operation if the existing API design supports it cleanly;
- `HumanReview -> Completed` is represented as an explicit human action rather than an arbitrary status assignment.

Do not create endpoints merely for internal automation.

A dedicated approval endpoint is justified only if it provides a real authorization/lifecycle boundary that the existing generic endpoint cannot safely express.

Potential shape:

```http
POST /api/projects/{id}/approve
```

Use the repository's existing routing/API conventions rather than blindly adopting this exact route.

---

## 6. Authorization / Actor Integrity

If the repository already has authenticated-user or role information, enforce that the final approval must come from an authorized human actor.

Do not invent a fake user identity.

If there is currently no real authentication/authorization abstraction available, do NOT build a complete authentication system in this phase.

Instead:

- preserve the explicit semantic approval boundary;
- use the simplest repository-compatible actor/audit mechanism available;
- document the exact limitation.

Do not claim "authorized human approval" merely because an HTTP request was received.

The implementation and tests must distinguish:

```text
agent/system transition
```

from:

```text
explicit human approval action
```

---

## 7. Auditability

Inspect whether the project already has an audit/event/history mechanism.

If one exists, use it for the approval event.

At minimum, the system should make the final approval observable through existing persistence/logging conventions.

Avoid introducing a large audit framework solely for this phase.

Record enough information to answer:

- which Project was approved;
- when approval occurred;
- what lifecycle state it was approved from;
- who/what initiated the approval, when actor infrastructure exists.

If the repository has no suitable audit infrastructure, keep the implementation minimal and explicitly document the limitation rather than inventing a large event-sourcing subsystem.

---

## 8. Legacy Pipeline Analysis

Phase 18.10 identified two independent Project-transition systems:

### New pipeline

```text
ProjectTransitions
Project.TransitionTo
ProjectService
TaskDispatchScheduler
AgentRun
AgentOrchestrator
IVerificationGate
ProjectCompletionGate
```

### Legacy pipeline

```text
WorkflowTransitionValidator
IWorkflowEngine
WorkflowStateStore
ApprovalService
AutonomousCodingOrchestrator
```

Do not merge these systems speculatively.

Instead, determine empirically:

1. Whether legacy approval code still mutates `Project.Status`.
2. Whether it can still produce `Completed`.
3. Whether it can bypass `HumanReview`.
4. Whether it is used by current tests/application flows.
5. Whether changing it would break legacy behavior.

Then choose the smallest safe solution.

Possible outcomes:

### Preferred

Legacy approval remains isolated from the new Phase 18.3+ lifecycle and cannot bypass the new Project lifecycle boundary.

### Acceptable

A minimal compatibility adapter routes legacy approval into the same explicit Project approval service.

### Not acceptable

Two independent mechanisms remain capable of completing the same new Project through contradictory lifecycle rules.

Do not rewrite the legacy orchestration subsystem in this phase.

---

## 9. Concurrency and Idempotency

The approval operation must use the repository's existing optimistic concurrency/version mechanism.

No:

- locks
- static ownership maps
- distributed locks
- process-local approval state

Required behavior:

### Concurrent approval requests

Two simultaneous approvals against the same `HumanReview` Project must result in:

- exactly one successful transition;
- the other request safely receiving an idempotent/already-completed or concurrency response according to existing API conventions.

### Repeated approval

A repeated approval after successful completion must never move the Project again or corrupt state.

### Invalid-state approval

Approval from:

- Created
- Analyzing
- Specification
- Architecture
- Planning
- Implementation
- Testing
- Reviewing
- Failed
- Cancelled

must not produce `Completed`.

Use existing transition validation rather than duplicating lifecycle rules in multiple places.

---

## 10. Failure Semantics

Do not introduce a new Project repair loop.

Do not change Phase 18.10 behavior:

- WorkItem verification failure remains a WorkItem repair concern.
- Project-level verification failure remains Project failure.
- Human approval does not rerun build/test.
- Human approval does not dispatch AgentRuns.
- Human approval does not modify WorkItems.

Approval is an authorization boundary, not another verification engine.

---

## 11. Required Invariants

At minimum, enforce and test these invariants:

```text
1. Agent/automatic execution cannot produce Project.Completed.
2. ProjectCompletionGate cannot produce Project.Completed.
3. Reviewing cannot be completed directly by the generic status API.
4. HumanReview is required before Project.Completed.
5. Only explicit approval can perform HumanReview -> Completed.
6. Approval from any other state is rejected.
7. Repeated approval is safe.
8. Concurrent approval is race-safe.
9. Failed/Cancelled projects cannot be approved.
10. WorkItem state is never modified by approval.
11. Approval does not invoke agents, scheduler dispatch, build, or tests.
```

---

## 12. Tests

Do not rerun or recreate the large Phase 18.10 completion-gate test matrix unless a changed behavior requires regression coverage.

Add focused tests for the newly changed boundary.

### Domain tests

Test:

- `Reviewing -> Completed` is no longer valid for the new lifecycle.
- `Reviewing -> HumanReview` remains valid.
- `HumanReview -> Completed` remains valid.
- invalid transitions remain rejected.

### Service tests

Test:

- successful approval from `HumanReview`;
- approval from `Reviewing` rejected;
- approval from `Testing` rejected;
- approval from `Implementation` rejected;
- approval from `Failed` rejected;
- approval from `Cancelled` rejected;
- repeated approval is safe;
- no WorkItem mutations;
- no AgentRun creation;
- no scheduler invocation;
- no verification invocation.

### API tests

Test the explicit approval operation, if a new endpoint is justified.

Verify:

- success response;
- invalid-state response;
- already-completed/idempotent response;
- concurrency conflict behavior;
- generic status endpoint cannot bypass HumanReview.

### Concurrency test

Use two independent `DbContext` instances and execute approval concurrently.

Verify:

```text
exactly one successful transition
Project.Status == Completed
no duplicate side effects
```

### End-to-end lifecycle test

Reuse the already-existing Phase 18.10 integration setup rather than recreating the entire execution pipeline.

Prove:

```text
Implementation
    -> Testing
    -> Reviewing
    -> HumanReview
    -> explicit human approval
    -> Completed
```

The final step must be performed through the new explicit approval boundary.

Also prove:

```text
Reviewing
    -> generic status API
    -> Completed
```

is no longer possible.

---

## 13. No Regressions

Do not modify behavior of:

- `TaskDispatchScheduler`
- `ProjectCompletionGate`
- `AgentRunWorker`
- `AgentOrchestrator`
- WorkItem verification
- WorkItem repair loop
- planner
- dependency resolution

unless required solely to enforce the approval boundary.

Do not create a second scheduler, second completion gate, or second verification system.

Do not add migration work unless actor/audit/approval persistence genuinely requires it.

Prefer zero schema changes.

---

## 14. Validation

Run only focused tests first.

Then:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Also run the migration/model consistency check when applicable:

```bash
dotnet ef migrations has-pending-model-changes \
  --startup-project ../AI.Factory.Api \
  --project .
```

Do not fabricate any result.

If the full suite regresses because of legacy approval behavior, identify the exact dependency and resolve it without weakening the HumanReview invariant.

---

## 15. Final Report

Create:

```text
.ai/context/phase-18.11-human-review-approval-integrity.md
```

Document:

- repository findings;
- exact pre-existing bypass;
- final lifecycle semantics;
- approval service/API design;
- authorization limitations, if any;
- legacy pipeline interaction;
- concurrency behavior;
- tests added;
- exact build/test results;
- migration status;
- known limitations;
- any remaining legacy conflict.

---

## 16. Failure Handling / NO-GO

Return **NO-GO** if any of the following remains true:

- `Reviewing -> Completed` can still bypass `HumanReview` in the new Project lifecycle;
- an automatic agent/scheduler path can produce `Completed`;
- approval does not enforce the current `HumanReview` state;
- concurrent approval can corrupt or duplicate completion;
- the implementation silently relies on fake actor identity;
- legacy and new pipelines can independently complete the same Project through contradictory rules and the conflict is not explicitly isolated.

Do not weaken the invariant just to make tests pass.

---

## 17. Definition of Done

Phase 18.11 is complete when:

```text
Project lifecycle:

Implementation
      |
      v
  Testing
      |
      v
 Reviewing
      |
      v
 HumanReview
      |
      | explicit human approval
      v
  Completed
```

is the authoritative lifecycle for the new Project pipeline.

The generic/manual status API cannot bypass `HumanReview`.

The approval operation is deterministic, concurrency-safe, idempotent, and auditable to the extent supported by the repository's existing actor/audit infrastructure.

No automatic code path can complete a Project.

No WorkItem execution/verification/repair behavior is duplicated or regressed.

Final report exists at:

```text
.ai/context/phase-18.11-human-review-approval-integrity.md
```

Final decision must be exactly one of:

```text
Human Review & Approval Integrity: VERIFIED
Final Decision: GO
```

or:

```text
Human Review & Approval Integrity: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> Agents produce implementation.
> Verification proves implementation.
> Project gates prove readiness.
> HumanReview establishes the approval boundary.
> Only explicit human approval authorizes final Project completion.