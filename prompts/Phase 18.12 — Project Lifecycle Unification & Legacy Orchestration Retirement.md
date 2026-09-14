# Phase 18.12 — Project Lifecycle Unification & Legacy Orchestration Retirement

## Phase Objective

Eliminate the remaining architectural split in Project lifecycle management.

Phase 18.11 established the authoritative completion boundary for the new pipeline:

`Implementation -> Testing -> Reviewing -> HumanReview -> Completed`

and made `ProjectService.ApproveAsync` the explicit completion operation.

However, the repository still contains a second, legacy Project-transition pipeline:

```text
ApprovalService
IWorkflowEngine
WorkflowEngine
WorkflowStateStore
WorkflowTransitionValidator
AutonomousCodingOrchestrator
```

which can also write `Project.Status`.

This phase must empirically determine whether that legacy Project lifecycle can now be retired, isolated, or safely adapted so that there is **one authoritative Project lifecycle state machine and one authoritative mechanism for Project status mutation**.

The goal is architectural consolidation, not a rewrite of the application.

---

## 1. Repository Inspection

Before modifying anything, inspect:

- `AGENTS.md`
- all Phase 18.x reports, especially 18.2, 18.5, 18.6, 18.10 and 18.11
- `Project`
- `ProjectStatus`
- `ProjectTransitions`
- `ProjectService`
- `ProjectService.ApproveAsync`
- all Project APIs
- `ApprovalRequest`
- `ApprovalService`
- `IWorkflowEngine`
- `WorkflowEngine`
- `WorkflowStateStore`
- `WorkflowTransitionValidator`
- `AutonomousCodingOrchestrator`
- all services/controllers/endpoints depending on those components
- all tests referencing:
  - `ApprovalService`
  - `WorkflowEngine`
  - `WorkflowStateStore`
  - `AutonomousCodingOrchestrator`
  - `/api/approvals`
  - Project status transitions

Search the whole repository for:

```text
Project.Status =
project.Status =
TransitionTo(
TransitionStatusAsync(
WorkflowStateStore
WorkflowTransitionValidator
IWorkflowEngine
ApprovalService
AutonomousCodingOrchestrator
```

Build a concrete dependency map before deciding what can be removed.

Do not assume the legacy system is unused merely because Phase 18.3+ is the desired architecture.

---

## 2. Establish the Target Architecture

The desired Project lifecycle authority is:

```text
Project
   |
   v
ProjectTransitions
   |
   v
ProjectService
```

For automatic progression:

```text
TaskDispatchScheduler
    -> ProjectCompletionGate
    -> Project.TransitionTo(...)
```

For final human completion:

```text
ProjectService.ApproveAsync
    -> Project.TransitionTo(Completed)
```

There must not be another independent component that can mutate the same Project lifecycle using a second transition table.

The target is:

```text
ONE Project lifecycle
ONE transition authority
ONE completion authority
```

---

## 3. Classify Every Legacy Component

For each of these:

```text
ApprovalRequest
ApprovalService
IWorkflowEngine
WorkflowEngine
WorkflowStateStore
WorkflowTransitionValidator
AutonomousCodingOrchestrator
ValidationRun
ReviewRun
```

classify it as exactly one of:

```text
A. Project-lifecycle obsolete
B. Still required for non-Project functionality
C. Can be adapted onto the new Project lifecycle
D. Must remain temporarily for compatibility
```

Do not delete code merely because it looks old.

Do not preserve code merely because existing tests reference it.

The decision must be based on actual production/application dependencies.

---

## 4. Preferred Resolution Order

Use the following order of preference.

### Preferred

Retire the legacy Project lifecycle path completely if empirical dependency analysis shows that Phase 18.3+ already provides all required Project lifecycle behavior.

This may include:

- removing direct Project status mutation from `WorkflowStateStore`;
- removing `WorkflowTransitionValidator` as a Project lifecycle authority;
- removing or isolating legacy Project approval behavior;
- deleting obsolete Project-level orchestration code;
- updating/removing only the tests that prove obsolete behavior.

### Second-best

If some legacy functionality still has legitimate consumers, adapt it so that it delegates Project lifecycle changes to the new authority:

```text
ProjectService
ProjectTransitions
ProjectService.ApproveAsync
```

Do not create an adapter that merely wraps one state machine with another independent state machine.

### Compatibility-only

If immediate retirement is impossible, isolate the legacy path so that:

- it cannot independently decide Project lifecycle semantics;
- it cannot introduce transitions not recognized by `ProjectTransitions`;
- it cannot directly write `Project.Status`;
- it delegates state changes to the new Project lifecycle service.

Document exactly why full retirement is not yet possible.

---

## 5. Eliminate Direct Project.Status Mutation

Search for every place that can modify Project status.

The final architecture should ensure that application code cannot casually perform:

```csharp
project.Status = ...
```

for lifecycle transitions.

Use:

```csharp
project.TransitionTo(...)
```

through the authoritative Project lifecycle service/path.

If direct status assignment is required during EF materialization, migration, test seeding, or controlled initialization, keep those cases narrowly scoped and document them.

The important invariant is:

> Runtime Project lifecycle transitions must not bypass `ProjectTransitions`.

---

## 6. Eliminate the Second Project Transition Table

If `WorkflowTransitionValidator` still defines an independent Project state machine, determine whether it can be removed.

The desired end state is that there is no competing semantic definition such as:

```text
WorkflowTransitionValidator
```

that independently decides which Project statuses are valid.

Do not simply duplicate the rules from `ProjectTransitions` into the old validator.

Prefer removing the old validator or making it delegate to the authoritative lifecycle.

There must be one source of truth.

---

## 7. Legacy Approval Behavior

Phase 18.11 established that the legacy approval path:

- can complete a Project;
- requires the Project to already be in `HumanReview`;
- can enter `HumanReview` from statuses such as `Implementation`, bypassing the new Testing/Reviewing pipeline.

This phase must resolve that architectural inconsistency.

Do not merely document it again.

Determine whether legacy approval can be:

```text
retired
```

or:

```text
redirected to ProjectService.ApproveAsync
```

or:

```text
retained only for a clearly isolated legacy workflow
```

If retained, it must not silently act as a second implementation of the new Project lifecycle.

---

## 8. AutonomousCodingOrchestrator

Inspect whether `AutonomousCodingOrchestrator` is still actively required.

Specifically identify whether it still:

- creates WorkItems;
- dispatches tasks;
- runs validation;
- requests approval;
- modifies Project status;
- depends on legacy workflow transitions.

Do not rewrite its entire implementation.

If it is obsolete for the current AI Factory pipeline, retire it cleanly.

If it is still required for an existing feature, separate its surviving responsibilities from Project lifecycle ownership.

The new AI Factory pipeline must not depend on the legacy orchestrator for:

```text
Planning
Implementation
Verification
Repair
Review
Approval
Completion
```

---

## 9. ApprovalRequest / ApprovalService Decision

Determine whether `ApprovalRequest` is still needed after the new:

```text
POST /api/projects/{id}/approve
```

and:

```text
ProjectService.ApproveAsync
```

If it is only supporting the obsolete legacy Project lifecycle:

- retire it or isolate it.

If it serves an independent business concern:

- preserve it,
- but ensure it does not redefine the new Project lifecycle.

Do not introduce duplicate approval records purely to preserve old architecture.

---

## 10. API Compatibility

Inspect existing:

```text
/api/approvals/*
/api/projects/*
```

Determine which APIs are now:

```text
current
legacy-but-required
obsolete
```

Do not remove a public endpoint without checking its tests and current callers.

If a legacy approval endpoint remains necessary, ensure its behavior is clearly isolated and cannot bypass the authoritative Project lifecycle.

The preferred Project completion API remains the explicit Project approval operation from Phase 18.11.

---

## 11. Data Compatibility

Before deleting any legacy service, inspect whether its entities or tables contain existing persisted data:

- `ApprovalRequest`
- `ValidationRun`
- `ReviewRun`
- workflow state records
- any related history

Do not perform destructive data deletion simply because the code becomes obsolete.

If code can be retired while the existing tables/data remain harmlessly unused, prefer that approach.

Only create a migration if a schema change is genuinely required.

Prefer:

```text
zero migration
```

where possible.

---

## 12. Invariants After Refactoring

The following must be true after this phase.

### Project lifecycle

```text
Implementation
    -> Testing
    -> Reviewing
    -> HumanReview
    -> Completed
```

### Authority

Only the authoritative Project lifecycle may change Project status.

### Automatic completion

No agent, scheduler, legacy workflow, or orchestrator can directly produce:

```text
ProjectStatus.Completed
```

### Final approval

Only:

```text
ProjectService.ApproveAsync
```

or a direct delegation to it can perform the new lifecycle's final approval.

### HumanReview

No active path may jump:

```text
Implementation -> HumanReview
```

for the new pipeline.

The authoritative lifecycle must require:

```text
Testing -> Reviewing -> HumanReview
```

before completion.

### Consistency

There must be no second independently maintained Project transition table.

---

## 13. Tests

Do not recreate the already-proven Phase 18.10/18.11 test suites.

Instead, add focused regression tests around the architectural consolidation.

### Authority tests

Prove that all runtime Project lifecycle transitions go through the authoritative mechanism.

### Legacy removal/isolation tests

If legacy components are removed:

- verify current functionality no longer references them.

If legacy components remain:

- verify they cannot mutate Project.Status directly;
- verify they delegate to the authoritative lifecycle;
- verify they cannot introduce legacy-only transitions.

### HumanReview invariant

Explicitly test that the retained application paths cannot perform:

```text
Implementation -> HumanReview
```

through the new Project lifecycle.

### Completion invariant

Test that no remaining legacy path can produce:

```text
Reviewing -> Completed
```

or otherwise bypass:

```text
HumanReview
```

### API regression

Test affected `/api/approvals/*` and `/api/projects/*` endpoints only where behavior changed.

### Existing legacy tests

Do not preserve tests solely because they encode obsolete architecture.

When a test represents behavior that this phase intentionally retires, replace it with a test of the new authoritative behavior and document the reason.

---

## 14. Concurrency

Preserve the existing:

```text
Project.Version
ConcurrencyConflictException
```

mechanism.

No locks.

No new ownership maps.

No second concurrency system.

If a legacy adapter delegates into `ProjectService`, ensure that optimistic concurrency still happens at the authoritative persistence boundary.

---

## 15. Observability

Preserve existing audit behavior.

In particular, final approval must continue to produce the existing:

```text
AuditEventType.HumanApproval
```

event through `IAuditLogger`.

Do not create a second approval audit mechanism.

If legacy approval is retired, confirm that the new approval path remains fully observable.

---

## 16. Validation

Start with focused tests around the legacy components being modified.

Then run:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Run migration consistency:

```bash
dotnet ef migrations has-pending-model-changes \
  --startup-project ../AI.Factory.Api \
  --project .
```

Also perform a repository-wide search after implementation for remaining runtime Project status mutation outside the authoritative lifecycle path.

The final report must identify every remaining legitimate exception.

Do not fabricate evidence.

---

## 17. Real Hermes

Real Hermes execution is not the primary target of this phase.

Do not spend this phase rerunning the already-established Hermes E2E suites unless the legacy removal materially changes an execution path and a focused real-Hermes regression is necessary.

The architectural result of this phase must be verifiable deterministically from the application and tests.

---

## 18. Failure Handling / NO-GO

Return **NO-GO** if:

- two independent Project lifecycle state machines still remain without a justified compatibility boundary;
- legacy code can still directly mutate Project.Status at runtime;
- legacy code can bypass the authoritative `HumanReview` boundary;
- both old and new approval mechanisms independently define lifecycle semantics;
- removing legacy behavior silently breaks a still-required application flow;
- a workaround introduces a third lifecycle/transition abstraction;
- existing architecture is only cosmetically renamed while the duplicate state authority remains.

Do not mark GO simply because the full test suite passes if the architectural split remains.

---

## 19. Final Report

Create:

```text
.ai/context/phase-18.12-project-lifecycle-unification.md
```

The report must include:

- complete dependency findings;
- every legacy Project lifecycle component discovered;
- classification of each component;
- what was retired;
- what was adapted;
- what remains and why;
- final Project lifecycle authority;
- evidence that direct Project status mutation is controlled;
- evidence that duplicate transition semantics are removed or isolated;
- API compatibility result;
- data/schema result;
- focused test results;
- full build/test results;
- migration status;
- remaining architectural risks.

---

## 20. Definition of Done

The phase is complete when the architecture has a single authoritative Project lifecycle.

Target architecture:

```text
                   +----------------------+
                   |   ProjectService     |
                   | Project lifecycle     |
                   +----------+-----------+
                              |
                              v
                   +----------------------+
                   | ProjectTransitions   |
                   +----------+-----------+
                              |
            +-----------------+-----------------+
            |                                   |
            v                                   v
 TaskDispatchScheduler                 ProjectService.ApproveAsync
 ProjectCompletionGate                         |
            |                                   |
            +-----------------+-----------------+
                              |
                              v
                    Project.Status
```

Legacy components may remain only when they have a clearly justified non-Project responsibility or compatibility role.

There must no longer be a second independent Project lifecycle authority capable of creating contradictory transitions.

Final lifecycle remains:

```text
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
      | explicit approval
      v
  Completed
```

Final decision must be exactly:

```text
Project Lifecycle Unification: VERIFIED
Final Decision: GO
```

or:

```text
Project Lifecycle Unification: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> One Project has one lifecycle state machine.
> One lifecycle has one source of truth.
> Legacy compatibility must not create a second authority.
> Agents execute work.
> Verification proves work.
> Project gates determine readiness.
> Human approval authorizes completion.