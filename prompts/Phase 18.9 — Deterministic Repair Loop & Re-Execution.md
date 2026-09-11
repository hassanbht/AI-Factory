# Phase 18.9 — Deterministic Repair Loop & Re-Execution

## Phase Objective

Close the lifecycle gap left by Phase 18.8:

```text
WorkItem
  Ready
    ↓
Running
    ↓
Testing
    ↓
Verification Failed
    ↓
Repairing
    ↓
[REPAIR AGENT]
    ↓
Testing / Re-execution
    ↓
Verification
    ↓
Completed
```

Phase 18.8 intentionally introduced `Repairing` but left it without an execution path.

This phase must implement a **deterministic, bounded repair loop** that consumes verification failures, invokes an appropriate repair AgentRun, re-executes the WorkItem, and returns it through the existing verification gate.

Do NOT redesign the existing scheduler, AgentRuntime, Hermes integration, verification gate, or recovery architecture.

The goal is to make the existing lifecycle actually progress from:

`Repairing -> Running -> Testing -> verification -> Completed`

while remaining deterministic, idempotent, concurrency-safe, and bounded.

---

# 1. Mandatory Repository Inspection

Before changing code, inspect:

- `AGENTS.md`
- Phase 18.7 report
- Phase 18.8 report
- `WorkItem`
- `WorkItemStatus`
- `WorkItemTransitions`
- `AgentRun`
- `AgentRunStatus`
- `AgentRunTransitions`
- `AgentDefinition`
- `TaskDispatchScheduler`
- `TaskDependencyResolver`
- `AgentRunWorker`
- `AgentRunWorkItemSync`
- `VerificationGate`
- `IVerificationGate`
- `WorkItemExecutionResultMapper`
- `IAgentRuntime`
- `AgentOrchestrator`
- existing AgentRun creation paths
- existing optimistic concurrency/version handling
- existing heartbeat/recovery
- existing retry/attempt fields, if any
- existing configuration/options patterns
- all relevant tests

Also inspect the Phase 18.8 final report before implementing anything.

Do not assume a new abstraction is required if an existing abstraction can be reused.

---

# 2. Current Known State

Phase 18.8 established:

```text
AgentRun Succeeded
    +
execution.status = "completed"
        ↓
WorkItem Running -> Testing
        ↓
IVerificationGate
        ↓
Passed       -> Completed
Failed       -> Repairing
InfrastructureError -> Failed
TimedOut     -> Failed
Cancelled    -> Cancelled
```

The known limitation is:

```text
Repairing
   ↓
(no execution path)
```

This phase closes that gap.

Do not remove or bypass the Phase 18.8 verification gate.

An agent claiming that it repaired a WorkItem is NOT sufficient proof.

Every repair attempt must eventually return through the deterministic verification gate.

---

# 3. Target Architecture

Implement this conceptual flow:

```text
                    ┌─────────────────────┐
                    │     WorkItem        │
                    │      Ready          │
                    └──────────┬──────────┘
                               │
                               ▼
                         Task Scheduler
                               │
                               ▼
                         AgentRun Worker
                               │
                               ▼
                         Implementation
                               │
                               ▼
                            Testing
                               │
                               ▼
                       VerificationGate
                         /           \
                     Passed          Failed
                       │                │
                       ▼                ▼
                   Completed        Repairing
                                         │
                                         ▼
                                  Repair AgentRun
                                         │
                                         ▼
                                     Running
                                         │
                                         ▼
                                   Testing
                                         │
                                         ▼
                                VerificationGate
```

The repair AgentRun must use the existing AgentRuntime/Hermes infrastructure.

Do not create a second runtime.

Do not invoke Hermes directly from the repair service.

---

# 4. Repair Trigger

A repair must be triggered only when:

```text
WorkItem.Status == Repairing
```

and the previous verification result indicates a repairable implementation failure.

The existing Phase 18.8 policy must remain authoritative:

- `VerificationStatus.Failed` → repairable
- `InfrastructureError` → not repairable automatically
- `TimedOut` → not automatically repairable unless the existing architecture explicitly supports this
- `Cancelled` → not repairable automatically

Do not reinterpret infrastructure failures as implementation failures.

---

# 5. Repair AgentDefinition

Inspect the existing `AgentDefinition.AgentType` conventions.

Introduce a dedicated repair agent type only if the current model requires one.

Prefer:

```text
Repair
```

as the canonical agent type.

Do not automatically seed an AgentDefinition unless the repository already has an established seeding mechanism that should own this.

The repair flow must fail safely if no enabled compatible AgentDefinition exists.

Do not silently select an arbitrary WorkItem agent.

---

# 6. Repair Input / Objective

The repair agent must receive deterministic context derived from Factory-owned state.

The objective should include, at minimum:

- Project identity
- WorkItem identity
- WorkItem title
- WorkItem description
- current verification failure summary
- previous implementation execution summary, if available
- bounded relevant artifact/test information already available
- explicit instruction to repair the implementation
- explicit instruction not to modify unrelated functionality
- explicit instruction that success will be independently verified

Do NOT allow the LLM to provide the verification command.

Do NOT use arbitrary commands from WorkItem.Description.

Do NOT trust an LLM-generated status as verification evidence.

The verification gate remains the source of truth.

Bound all injected failure/output text to avoid unbounded AgentRun input.

---

# 7. Repair AgentRun Creation

Create a normal `AgentRun` through the existing AgentRun creation/orchestration infrastructure.

Required linkage:

```text
AgentRun.ProjectId  = WorkItem.ProjectId
AgentRun.WorkItemId = WorkItem.Id
```

The AgentRun must be distinguishable as a repair execution.

Prefer using existing AgentRun/AgentDefinition fields rather than introducing a new schema field.

If a new field is genuinely required, first prove that existing fields cannot represent the state.

Avoid migration unless absolutely necessary.

---

# 8. Deterministic Repair Dispatch

Do not create a second scheduler.

Extend the existing dispatch mechanism only where necessary.

The scheduler must recognize:

```text
Repairing
```

as eligible for repair execution, but must NOT treat `Repairing` as a normal implementation `Ready` task.

Preserve dependency semantics:

```text
Completed = satisfies dependency
Testing   = does not satisfy dependency
Repairing = does not satisfy dependency
Running   = does not satisfy dependency
Ready     = does not satisfy dependency
```

A dependent WorkItem must remain blocked/waiting while its dependency is being repaired.

Do not modify `TaskDependencyResolver` semantics unless absolutely required.

---

# 9. Repair Concurrency / Idempotency

The repair dispatch must be race-safe.

Two scheduler/worker observations must not create two simultaneous repair AgentRuns for the same WorkItem.

Use the existing optimistic concurrency/version mechanism.

Do NOT introduce:

- static locks
- distributed locks
- singleton ownership dictionaries
- process-local global state

The desired behavior is:

```text
Repairing WorkItem
        ↓
one repair AgentRun created
        ↓
WorkItem Running
```

A duplicate observation must detect that another writer already claimed the WorkItem and return safely.

---

# 10. WorkItem State Transition

Use the existing lifecycle transitions wherever possible.

Expected repair execution:

```text
Repairing -> Running
```

After the repair AgentRun completes:

```text
Running -> Testing
```

Then the existing Phase 18.8 verification gate runs.

Do not add a direct:

```text
Repairing -> Completed
```

transition.

Do not allow the repair agent to bypass verification.

If the existing transition table does not allow `Repairing -> Running`, inspect why and make the smallest domain change necessary.

Do not redesign WorkItem lifecycle.

---

# 11. Repair Attempt Limit

The repair loop MUST be bounded.

Inspect whether an existing attempt/retry counter already exists.

If one exists, reuse it.

If none exists, add the smallest persistent field necessary, for example:

```text
RepairAttemptCount
```

with a deterministic default of `0`.

Only introduce a migration if required.

Add configuration:

```text
MaxRepairAttempts
```

with a safe finite default.

The exact default should follow existing configuration conventions and should not be excessive.

Example policy:

```text
attempt < MaxRepairAttempts
    -> create repair AgentRun

attempt >= MaxRepairAttempts
    -> WorkItem Failed
```

Never create an infinite repair loop.

The attempt counter must be updated atomically with the repair claim.

---

# 12. Failure After Maximum Repairs

When the repair budget is exhausted:

```text
Repairing -> Failed
```

Set:

```text
WorkItem.CurrentError
```

to a bounded deterministic message containing:

- maximum repair attempts reached
- latest verification failure summary

Do not invoke another agent.

Do not leave the WorkItem permanently in `Repairing`.

This is a terminal fail-closed condition.

---

# 13. Successful Repair

A successful repair agent execution does NOT mean the WorkItem is completed.

The only successful path is:

```text
Repair AgentRun
    ↓
Succeeded
    ↓
WorkItem Testing
    ↓
VerificationGate
    ↓
Passed
    ↓
Completed
```

This must be enforced by code.

If the repair agent returns:

```text
execution.status = "completed"
```

that only means the repair agent finished its work.

It is not proof that the implementation works.

---

# 14. Repair Agent Failure

If the repair AgentRun itself ends in:

```text
Failed
TimedOut
Cancelled
```

apply a deterministic policy.

Prefer:

- `Failed` → retry repair if budget remains
- `TimedOut` → retry only if existing retry semantics explicitly support it; otherwise `Failed`
- `Cancelled` → `Cancelled`

Do not accidentally turn infrastructure/runtime failure into an implementation verification failure.

Document the exact policy in code/tests.

---

# 15. Verification Failure Feedback

After each failed verification:

```text
VerificationGate
      ↓
Repairing
```

the next repair AgentRun must receive the latest failure information.

Do not accumulate unbounded historical logs into the prompt.

Use only bounded current/necessary context.

The repair agent should be able to distinguish:

```text
Previous implementation
+
Latest verification failure
```

from generic project requirements.

---

# 16. Heartbeat / Recovery

Reuse Phase 18.4 heartbeat behavior.

A repair AgentRun linked to a WorkItem must receive the same WorkItem heartbeat treatment as a normal implementation AgentRun.

Do not create a second heartbeat implementation.

Inspect the current `AgentRunWorker` behavior and extend it only if the repair path is not already covered automatically.

Verification remains subject to the Phase 18.8 known recovery limitation unless this phase can fix it as a direct, minimal consequence.

Do NOT create a generic recovery framework in this phase.

---

# 17. Scheduler Semantics

Preserve deterministic ordering.

Normal Ready tasks and Repairing tasks must not produce nondeterministic execution order.

Define an explicit deterministic policy, for example:

1. Repairing tasks eligible for repair
2. Ready tasks
3. stable ordering by ProjectId / WorkItemId / PlanKey

OR another ordering justified by the existing scheduler design.

The chosen policy must be deterministic and tested.

Do not starve Ready work indefinitely because of repairs.

Document the ordering rule.

---

# 18. API Surface

Do not add a manual "repair now" HTTP endpoint unless the existing architecture clearly requires one.

The repair loop should be internal orchestration.

If an existing API exposes WorkItem lifecycle/state, ensure the new states and attempt information are represented correctly.

Do not add an API merely to make the phase look complete.

---

# 19. Tests — Mandatory

Add deterministic tests for the complete repair lifecycle.

At minimum:

### Repair dispatch

1. `RepairingWorkItem_CreatesRepairAgentRun`
2. `RepairingWorkItem_UsesRepairAgentDefinition`
3. `RepairingWorkItem_LinksProjectAndWorkItem`
4. `RepairingWorkItem_TransitionsToRunning`

### Idempotency / concurrency

5. `ConcurrentRepairDispatch_CreatesOnlyOneRepairAgentRun`
6. `DuplicateRepairObservation_IsSafe`
7. `RepairClaim_UsesOptimisticConcurrency`

### Verification loop

8. `RepairAgentSucceeded_ReturnsToTesting`
9. `RepairAgentCannotDirectlyCompleteWorkItem`
10. `VerificationPassedAfterRepair_CompletesWorkItem`

### Repair failure

11. `VerificationFailedAfterRepair_ReentersRepairing`
12. `MaxRepairAttempts_FailsWorkItem`
13. `RepairAgentFailure_UsesDeterministicPolicy`
14. `RepairAgentCancellation_CancelsWorkItem`

### Dependency behavior

15. `RepairingDependency_DoesNotSatisfyDependentWorkItem`
16. `CompletedAfterRepair_SatisfiesDependentWorkItem`

### Persistence

17. `RepairAttemptCount_IsPersisted`
18. `RepairClaim_IsAtomic`

### Safety

19. `MissingRepairAgentDefinition_FailsClosed`
20. `RepairInput_IsBounded`
21. `VerificationCommands_AreStillFactoryOwned`

Use real SQLite for concurrency/persistence integration tests where appropriate.

Do not replace all integration coverage with mocks.

---

# 20. End-to-End Integration Test

Create one complete deterministic integration test covering:

```text
Project
  ↓
WorkItem
  ↓
initial implementation AgentRun
  ↓
verification fails
  ↓
WorkItem Repairing
  ↓
repair AgentRun
  ↓
WorkItem Testing
  ↓
verification passes
  ↓
WorkItem Completed
```

Use fake AgentRuntime and fake verification where deterministic orchestration is required.

The test must prove the actual persisted lifecycle and scheduler/worker wiring.

---

# 21. Real Hermes Test

Add one opt-in real Hermes test only if the existing Phase 18.7/18.8 infrastructure makes this straightforward.

Use the existing:

```text
AI_FACTORY_HERMES_SMOKE_TEST
```

mechanism and OmniRoute configuration.

Do not make the phase dependent on external LLM availability.

If not executed, explicitly report:

```text
Real Repair Hermes E2E: NOT RUN
```

Never claim real Hermes evidence without execution.

---

# 22. No Regression / No Redesign

Do NOT:

- replace Hermes
- replace OmniRoute
- redesign AgentRuntime
- redesign AgentOrchestrator
- redesign TaskDependencyResolver
- create another scheduler
- create another process runner
- create another verification engine
- bypass VerificationGate
- trust LLM completion as proof
- create an infinite repair loop
- introduce distributed locks
- redesign recovery
- add unrelated API functionality
- rewrite the legacy ValidationEngine pipeline
- redo Phase 18.7 or 18.8 functionality unnecessarily

Reuse existing infrastructure.

---

# 23. Validation Gates

Run:

```text
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Also run migration validation if the schema changed:

```text
dotnet ef migrations has-pending-model-changes \
  --project ../AI.Factory.Infrastructure \
  --startup-project .
```

Run targeted repair tests separately first.

Then run the complete suite.

Do not report only targeted tests.

Do not fabricate results.

---

# 24. Empirical Acceptance Criteria

The phase is GO only if all are true:

- `Repairing` WorkItems can be deterministically claimed.
- Exactly one repair AgentRun can claim a WorkItem under concurrency.
- Repair AgentRun is linked to Project and WorkItem.
- Repair execution uses the existing AgentRuntime.
- Repair completion moves the WorkItem to `Testing`.
- VerificationGate always runs after repair.
- Verification success is the only path to `Completed`.
- Verification failure can re-enter `Repairing`.
- Repair attempts are bounded.
- Exhausted repair budget results in terminal `Failed`.
- Dependencies are not satisfied by `Testing` or `Repairing`.
- Completed repaired dependencies satisfy dependents.
- No arbitrary command comes from agent output.
- No infinite repair loop exists.
- Existing Phase 18.7/18.8 behavior remains intact.
- Full test suite passes.
- Debug and Release builds pass.
- Migration state is clean if applicable.

---

# 25. Required Final Report

Create:

```text
.ai/context/phase-18.9-repair-loop.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Existing Infrastructure Reused
4. Exact Gap Before Phase 18.9
5. Architecture After Phase 18.9
6. Repair Trigger Policy
7. Repair AgentRun Design
8. Attempt / Retry Policy
9. Concurrency / Idempotency
10. Verification Integration
11. Dependency Semantics
12. Recovery / Heartbeat Behavior
13. Tests Added
14. Exact Build/Test Results
15. Migration Status
16. Real Hermes E2E Result
17. Known Limitations
18. Remaining Risks
19. Final Decision

The final section MUST contain exactly one of:

```text
Repair Loop & Re-Execution: VERIFIED
Final Decision: GO
```

or:

```text
Repair Loop & Re-Execution: NOT VERIFIED
Final Decision: NO-GO
```

Do not declare GO unless the empirical acceptance criteria are actually satisfied.

---

# Definition of Done

Phase 18.9 is complete when a WorkItem can demonstrably travel through:

```text
Ready
  ↓
Running
  ↓
Testing
  ↓
Repairing
  ↓
Running
  ↓
Testing
  ↓
Completed
```

with:

- deterministic repair dispatch,
- bounded attempts,
- optimistic-concurrency safety,
- existing Hermes/AgentRuntime execution,
- independent build/test verification,
- correct dependency semantics,
- no infinite loops,
- and full regression coverage.

The Factory must now be capable of **detecting an implementation failure, assigning the WorkItem back to an agent for repair, and independently proving the repaired result before declaring the WorkItem complete.**