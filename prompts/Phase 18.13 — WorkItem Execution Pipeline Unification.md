# Phase 18.13 — WorkItem Execution Pipeline Unification

## Phase Objective

Unify the remaining two independent WorkItem execution pipelines into the new Phase 18.3+ Agent-based execution architecture.

Current architecture contains two separate execution paths:

### New pipeline

```text
TaskDispatchScheduler
    -> AgentRun
    -> AgentRunWorker
    -> AgentOrchestrator
    -> IAgentRuntime / Hermes
    -> StructuredAgentResult
    -> WorkItem execution
    -> Verification
    -> Repair
```

### Remaining legacy execution path

```text
POST /api/orchestration-runs
    -> OrchestrationRun
    -> OrchestrationRunWorker
    -> OrchestrationRunExecutionEngine
    -> AutonomousCodingOrchestrator
    -> WorkItem build/test/review/repair/evaluation/learning
```

Phase 18.12 intentionally preserved the legacy path because it is still live and load-bearing. It is now the principal remaining architectural duplication.

This phase must determine whether the legacy WorkItem execution path can be retired, delegated into the new AgentRun pipeline, or must temporarily remain as a compatibility facade.

The desired end state is:

```text
ONE WorkItem execution architecture
```

with the Phase 18.3+ AgentRun pipeline as the authoritative implementation.

Do not redesign Project lifecycle. That work is already complete.

---

## 1. Repository Inspection

Before changing code, inspect:

- `AGENTS.md`
- Phase 18.3 through 18.12 reports
- `WorkItem`
- `WorkItemStatus`
- `WorkItemTransitions`
- `TaskDispatchScheduler`
- `AgentRun`
- `AgentRunWorker`
- `AgentOrchestrator`
- `IAgentRuntime`
- `IAgentInstructionBuilder`
- `StructuredAgentResult`
- `AgentRunWorkItemSync`
- `IVerificationGate`
- `ProjectCompletionGate`
- `RepairAttemptCount`
- `OrchestrationRun`
- `OrchestrationRunWorker`
- `OrchestrationRunExecutionEngine`
- `AutonomousCodingOrchestrator`
- `ValidationRun`
- `ReviewRun`
- `RepairOptions`
- `EvaluationEngine`
- `IApprovalRepository`
- `ApprovalRequest`
- `/api/orchestration-runs`
- all orchestration-run tests
- all autonomous-coding tests
- all WorkItem execution/verification/repair tests

Search the entire repository for:

```text
OrchestrationRun
OrchestrationRunWorker
OrchestrationRunExecutionEngine
AutonomousCodingOrchestrator
ExecuteSpecificWorkItemAsync
ValidationRun
ReviewRun
ApprovalRequest
IApprovalRepository
EvaluationEngine
```

Do not assume any legacy component is dead.

Produce a concrete dependency map before deleting or redirecting anything.

---

## 2. Define the Responsibility Matrix

Build a responsibility matrix comparing both execution pipelines.

At minimum compare:

```text
task selection
dependency handling
AgentRun creation
agent invocation
prompt/instruction construction
LLM/runtime interaction
structured result handling
build
test
verification
repair
attempt counting
heartbeat
cancellation
timeout
logging
evaluation
learning
artifact handling
API exposure
background execution
concurrency
recovery
```

For every capability identify:

```text
NEW
LEGACY
BOTH
NEITHER
```

The final implementation must not accidentally remove a capability that only exists in the legacy path.

---

## 3. Preferred Target Architecture

The preferred architecture is:

```text
                   +---------------------+
                   | TaskDispatchScheduler|
                   +----------+----------+
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
                           Hermes
                              |
                              v
                   StructuredAgentResult
                              |
                              v
                       WorkItem state
                              |
                    +---------+---------+
                    |                   |
                    v                   v
               Verification           Repair
```

`OrchestrationRun` should not remain a second implementation of WorkItem execution.

If the public `/api/orchestration-runs` feature is still needed, the preferred solution is to make it a thin compatibility/command layer that submits or references the authoritative AgentRun/WorkItem execution pipeline rather than executing WorkItems itself.

Do not maintain two engines that perform the same work differently.

---

## 4. Analyze OrchestrationRun's Real Product Semantics

Do not assume `/api/orchestration-runs` is merely an old API.

Determine empirically what callers expect.

Inspect:

- request/response contracts;
- persistence model;
- attempt/log endpoints;
- cancellation semantics;
- status semantics;
- whether one OrchestrationRun targets a Project, WorkItem, or arbitrary task;
- whether it supports manual execution that the scheduler does not;
- whether evaluation/learning results are externally consumed.

Classify the feature as:

```text
A. obsolete
B. compatibility API
C. legitimate manual execution feature
D. fundamentally different workflow that must remain separate
```

The preferred result is B or C implemented as a facade over the new execution infrastructure.

---

## 5. Do Not Rewrite AutonomousCodingOrchestrator Blindly

`AutonomousCodingOrchestrator` currently contains legitimate WorkItem execution behavior including build/test/review/repair/evaluation/learning. Phase 18.12 explicitly left this path in place because the feature is still live.

Determine which of its responsibilities are now duplicated by:

- `AgentOrchestrator`
- `IVerificationGate`
- WorkItem repair loop
- `AgentRunWorkItemSync`
- structured result handling

Create a responsibility map.

Then move or retire only the duplicated responsibilities.

Do not delete evaluation/learning behavior merely because its execution host is legacy.

---

## 6. Establish AgentRun as the Execution Primitive

The authoritative execution primitive must be:

```text
AgentRun
```

Every new WorkItem execution requested by the system should ultimately be represented by an `AgentRun`.

A legacy API request may create an AgentRun, but it must not create a second independent executor.

Desired semantic flow:

```text
Legacy/manual request
        |
        v
create/queue AgentRun
        |
        v
existing AgentRunWorker
        |
        v
existing AgentOrchestrator
```

Do not introduce another worker loop.

Do not introduce another process-management abstraction.

Do not introduce another status machine.

---

## 7. OrchestrationRun as a Compatibility Facade

If `OrchestrationRun` must remain for API compatibility, reduce it to orchestration metadata rather than execution ownership.

Possible target:

```text
OrchestrationRun
    |
    +--> ProjectId
    +--> WorkItemId
    +--> AgentRunId
    +--> request metadata
    +--> compatibility status
```

The exact model must follow the existing repository rather than this example.

The critical invariant is:

```text
OrchestrationRun does NOT execute the WorkItem itself.
```

Its worker must not independently call build/test/LLM/repair logic.

---

## 8. Cancellation

Preserve current cancellation semantics.

A cancellation request from:

```text
POST /api/orchestration-runs/{id}/cancel
```

must ultimately operate on the authoritative AgentRun execution when the feature is converted to a facade.

Do not create a second cancellation mechanism.

Verify interaction with:

- `AgentRunStatus.Cancelled`
- `AgentRunWorker`
- runtime cancellation token
- WorkItem cancellation mapping
- heartbeat cleanup

---

## 9. Timeout and Failure Semantics

The final architecture must have one authoritative interpretation of:

```text
Succeeded
Failed
TimedOut
Cancelled
```

for execution.

Do not preserve two competing failure-state mappings.

If `OrchestrationRun` has its own status fields, either:

- derive them from AgentRun, or
- explicitly document them as compatibility projection fields.

Do not allow the two statuses to diverge silently.

---

## 10. Verification and Repair Must Stay Factory-Owned

The new architecture already guarantees:

```text
agent reports completion
        ->
factory verification
        ->
Completed only after verification
```

and:

```text
verification failure
        ->
bounded WorkItem repair
        ->
re-execution
        ->
verification
```

Do not reintroduce legacy behavior where `AutonomousCodingOrchestrator` independently decides that a WorkItem is completed based on its own build/test/review interpretation.

There must be exactly one authoritative WorkItem completion path.

---

## 11. EvaluationEngine / ApprovalRequest Cleanup

Phase 18.12 identified a new dead dependency:

```text
EvaluationEngine
    -> IApprovalRepository
    -> ApprovalRequest
```

Production no longer creates ApprovalRequest records, so the `humanApproval` evaluation metric now effectively receives no meaningful new data.

This phase should inspect this dependency while consolidating execution.

Do not automatically delete it.

Determine:

1. whether `humanApproval` has any current external consumer;
2. whether it still represents a meaningful metric in the new AgentRun lifecycle;
3. whether it can be replaced with an explicit Project/WorkItem approval state;
4. whether the metric should be retired;
5. whether `ApprovalRequest` has any other current consumer.

Preferred outcome:

- remove the dead execution-time dependency if empirical evidence shows it has no valid producer;
- retain `ApprovalRequest` persistence only if historical data or another legitimate consumer requires it.

Do not invent a replacement metric merely to preserve a legacy field.

---

## 12. Persistence Strategy

Prefer no schema migration.

If `OrchestrationRun` can reference an existing AgentRun without schema changes, use the smallest possible solution.

If a migration is genuinely necessary, explain:

- why it is required;
- why existing fields cannot be reused;
- whether nullable/additive schema is sufficient;
- how existing data remains valid.

Do not destructively delete legacy tables in this phase unless the repository proves they are unused and the removal is unquestionably safe.

---

## 13. API Compatibility

Preserve the public `/api/orchestration-runs` API if it is still a supported feature.

The API should eventually represent:

```text
manual execution request
        ->
authoritative AgentRun
```

rather than:

```text
manual execution request
        ->
second executor
```

Preserve existing endpoints where practical:

```text
POST /api/orchestration-runs
GET /api/orchestration-runs/{id}
GET /api/orchestration-runs/{id}/attempts
GET /api/orchestration-runs/{id}/logs
POST /api/orchestration-runs/{id}/cancel
```

Do not preserve an endpoint merely to keep obsolete internals alive.

---

## 14. Concurrency and Recovery

Reuse existing mechanisms:

```text
AgentRun.Version
WorkItem.Version
heartbeat
WorkflowRecoveryHostedService
TaskSchedulerHostedService
```

Do not add another worker ownership model.

Prove that concurrent requests cannot create duplicate execution for the same logical manual request unless the existing API explicitly permits repeated independent runs.

Prove that a process restart cannot leave an OrchestrationRun executing independently from its AgentRun.

---

## 15. Tests

Do not rerun the entire historical legacy test matrix simply for coverage.

Add focused tests for the architectural transition.

### Required tests

#### Manual execution

Prove:

```text
POST /api/orchestration-runs
    -> AgentRun created
    -> AgentRunWorker executes
```

using existing test doubles.

#### Single executor

Prove the manual execution path does not invoke:

```text
AutonomousCodingOrchestrator
```

when it has been successfully converted to the AgentRun path.

#### Cancellation

Prove:

```text
OrchestrationRun cancel
    -> AgentRun cancellation
    -> correct terminal states
```

#### Failure

Prove AgentRun failure projects correctly to the public OrchestrationRun API.

#### Timeout

Same for timeout.

#### Idempotency / concurrency

Prove duplicate/manual concurrent requests follow the intended existing semantics and do not accidentally execute the same WorkItem twice.

#### Verification

Prove manual execution still reaches the existing deterministic verification gate.

#### Repair

Where applicable, prove verification failure enters the existing repair/re-execution pipeline rather than legacy repair logic.

#### Evaluation

If `EvaluationEngine` changes, add focused tests proving its new semantics and explicitly remove tests for behavior intentionally retired.

---

## 16. Deletion Criteria

Legacy components may be deleted only when:

1. no production code depends on them;
2. no supported API requires them;
3. their unique responsibilities have been preserved elsewhere;
4. their tests are either replaced by authoritative new-path tests or are intentionally retired;
5. persisted data remains safe.

Candidates include:

```text
AutonomousCodingOrchestrator
OrchestrationRunWorker
OrchestrationRunExecutionEngine
legacy execution helpers
```

But do not delete any of these merely because they are old.

The report must explicitly justify every deletion.

---

## 17. No Scope Creep

Do not:

- redesign Project lifecycle;
- redesign ProjectCompletionGate;
- redesign WorkItem verification;
- redesign WorkItem repair;
- redesign Hermes;
- replace OpenRouter/OmniRoute;
- add authentication;
- create a new workflow engine;
- create a second scheduler;
- build a new event-sourcing architecture;
- redesign EvaluationEngine beyond what is necessary to remove the dead ApprovalRequest execution dependency.

This phase is about **one WorkItem execution architecture**.

---

## 18. Validation

Run focused tests for the converted manual execution path first.

Then:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

Run:

```bash
dotnet ef migrations has-pending-model-changes \
  --startup-project ../AI.Factory.Api \
  --project .
```

Perform repository-wide searches after the implementation:

```text
AutonomousCodingOrchestrator
OrchestrationRunWorker
OrchestrationRunExecutionEngine
ExecuteSpecificWorkItemAsync
ApprovalRequest
IApprovalRepository
```

Report every remaining production reference and why it remains.

Do not claim that the pipelines are unified unless the repository evidence supports it.

---

## 19. Real Hermes

A real Hermes E2E test is recommended only if the manual `/api/orchestration-runs` path is converted to use the real AgentRun/Hermes execution path and the existing real-Hermes tests can exercise that route meaningfully.

Do not rerun unrelated Hermes tests merely for repetition.

A deterministic fake-runtime integration test is sufficient to prove the architectural routing.

---

## 20. Failure Handling / NO-GO

Return **NO-GO** if:

- two independent WorkItem execution engines still perform the same execution responsibility without a justified boundary;
- `/api/orchestration-runs` still launches a second executor instead of using AgentRun;
- `AutonomousCodingOrchestrator` remains the hidden authoritative completion mechanism;
- manual execution bypasses deterministic Factory verification;
- manual execution bypasses the bounded repair loop;
- execution status can diverge between OrchestrationRun and AgentRun without explicit compatibility semantics;
- cancellation/timeout behaves differently depending on which executor was used;
- dead `ApprovalRequest` execution dependencies are silently preserved without justification;
- deletion of legacy code removes still-supported functionality.

Passing tests alone is not sufficient if the two execution authorities remain architecturally duplicated.

---

## 21. Final Report

Create:

```text
.ai/context/phase-18.13-workitem-execution-unification.md
```

Document:

- dependency analysis;
- responsibility matrix;
- target architecture;
- classification of `OrchestrationRun`;
- classification of `AutonomousCodingOrchestrator`;
- what was retired;
- what was retained;
- what became a compatibility facade;
- AgentRun integration;
- cancellation/timeout mapping;
- verification/repair preservation;
- EvaluationEngine / ApprovalRequest decision;
- API compatibility;
- persistence/schema result;
- focused tests;
- full build/test results;
- migration status;
- remaining risks.

---

## 22. Definition of Done

The preferred final architecture is:

```text
                    WorkItem
                       |
              +--------+--------+
              |                 |
         Scheduler          Manual API
              |                 |
              +--------+--------+
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
                    Hermes
                       |
                       v
              StructuredAgentResult
                       |
                       v
                 WorkItem State
                       |
              +--------+--------+
              |                 |
              v                 v
        Verification         Repair
              |                 |
              +--------+--------+
                       |
                       v
                  Completed
```

`OrchestrationRun`, when retained, is only a compatibility/request-tracking layer.

There is no second WorkItem executor with its own independent completion, verification, repair, or cancellation semantics.

The new AgentRun pipeline is the single authoritative WorkItem execution architecture.

Final decision must be exactly:

```text
WorkItem Execution Unification: VERIFIED
Final Decision: GO
```

or:

```text
WorkItem Execution Unification: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> One WorkItem.
> One execution architecture.
> One AgentRun execution primitive.
> One verification authority.
> One repair loop.
> Compatibility APIs may remain, but they must not create a second executor.