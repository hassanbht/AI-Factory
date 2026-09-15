# Phase 18.19 — Live Hermes Acceptance & Production-Path Hardening

## Phase Objective

Turn the already-proven deterministic Golden Path into a genuinely verified live production-like path by executing it with:

- real Docker;
- real Hermes;
- real model/provider routing through the configured OmniRoute/OpenRouter-compatible endpoint;
- a real Git-initialized Project;
- real WorkItem AgentRuns;
- real workspace modification;
- real Factory verification;
- real repair/re-execution;
- real Project completion and human approval.

Phase 18.18 already established the deterministic acceptance architecture and added:

```text
RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled
```

but the reported validation did not include an actual live execution result.

This phase therefore does NOT primarily add new architecture.

Its main objective is:

> Execute the real product path, observe the exact failures that occur in a real environment, fix only the production-path issues required for the complete flow, and produce hard evidence that the AI Factory can execute one Project from creation to completion.

---

# 1. Repository Inspection

Before changing code, inspect:

- `AGENTS.md`
- Phase 18.16 report
- Phase 18.17 report
- Phase 18.18 report
- `RealHermesE2ETests`
- `GoldenProjectLifecycleE2ETests`
- `GoldenProjectFixture`
- `AgentDefinitionBootstrapper`
- `ProjectService`
- `ProjectSourceInitializer`
- `ProjectWorkspaceProvisioner`
- `IWorkspaceResolver`
- `AgentOrchestrator`
- `AgentRunWorker`
- `TaskDispatchScheduler`
- `HermesAgentRuntime`
- `HermesAgentExecutor`
- `WorkItemCompletionProcessor`
- `VerificationGate`
- `ProjectCompletionGate`
- `ProjectService.ApproveAsync`
- current Docker configuration
- Hermes configuration
- OmniRoute/OpenRouter configuration
- test environment variables
- all real-Hermes gating logic

Do not rewrite the existing E2E harness until understanding exactly what it already does.

---

# 2. Live Environment Prerequisites

Determine the exact prerequisites from the repository.

At minimum verify:

```text
Docker daemon
Hermes execution image/runtime
provider/router endpoint
API key configuration
AI_FACTORY_HERMES_SMOKE_TEST=true
```

Also verify:

```text
docker --version
docker info
Hermes image availability
provider connectivity
```

Use the project's actual commands/configuration rather than inventing new ones.

Never expose or print secrets.

---

# 3. First Live Run — No Code Changes

Before implementing anything, run the existing Phase 18.18 Real Hermes test exactly as currently implemented.

Do NOT modify the test first.

The purpose is to establish the actual baseline.

Record:

```text
PASS
FAIL
SKIPPED
INFRASTRUCTURE UNAVAILABLE
```

Do not interpret "test discovered" or "test compiled" as execution evidence.

The report must explicitly state whether:

```text
RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled
```

actually executed.

---

# 4. Live Execution Stages

The live test must prove the following sequence.

```text
Project creation
      ↓
Workspace provisioning
      ↓
Git source initialization
      ↓
WorkItem creation
      ↓
AgentDefinition resolution
      ↓
TaskDispatchScheduler
      ↓
AgentRun
      ↓
AgentRunWorker
      ↓
AgentOrchestrator
      ↓
HermesAgentRuntime
      ↓
Docker
      ↓
Hermes
      ↓
LLM/provider
      ↓
real source modification
      ↓
Factory Verification
      ↓
Repair if required
      ↓
Verification again
      ↓
WorkItem.Completed
      ↓
Project Testing
      ↓
Reviewing
      ↓
HumanReview
      ↓
explicit approval
      ↓
Project.Completed
```

Do not manually mutate lifecycle states in the live test.

The only intentional manual lifecycle operation is:

```text
HumanReview -> Completed
```

through:

```text
ProjectService.ApproveAsync
```

or its public API.

---

# 5. Real Hermes Must Be Truly Real

The live acceptance test must not use:

```text
FakeAgentRuntime
FakeAgentExecutor
FakeVerificationGate
manually completed WorkItems
manually completed AgentRuns
```

for its primary path.

Allowed:

- test database;
- temporary filesystem;
- local Git repository fixture;
- test doubles only for infrastructure not relevant to the specific live assertion.

The core Agent execution must be real.

---

# 6. Verify the Actual Docker Mount

The live test must prove the previously solved Phase 18.14 problem is actually solved in reality.

The container must receive:

```text
Project.RootPath
```

as its mounted workspace.

Verify that the container can see:

```text
Demo.slnx
src/
tests/
```

and that the host-side Project.RootPath changes after Agent execution.

Do not accept:

```text
Docker process exited successfully
```

as sufficient evidence.

The test must prove source visibility.

---

# 7. Verify Real File Modification

The Agent must perform a small controlled code change.

Before execution:

```text
MARKER_BEFORE
```

After execution:

```text
MARKER_AFTER
```

The host filesystem must contain the changed file after Hermes exits.

Do not trust the LLM response as evidence.

The test must independently inspect:

```text
Project.RootPath
```

after Agent execution.

---

# 8. Factory Verification Must Be Real

After the Agent modifies the source:

```text
VerificationGate
```

must run actual:

```text
dotnet build
dotnet test
```

against the same Project workspace.

Prove independently:

```text
Agent workspace
==
host Project.RootPath
==
Verification workspace
```

Do not only compare string values.

The final build/test must pass on the source actually modified by Hermes.

---

# 9. Real Repair Scenario

The live acceptance scenario must include one deliberate verification failure.

Do not manually set:

```text
WorkItemStatus.Repairing
```

Instead:

```text
real Agent
    ->
incorrect source
    ->
real dotnet test fails
    ->
AgentRunWorkItemSync
    ->
WorkItem.Repairing
```

Then:

```text
Repair AgentRun
    ->
real Hermes
    ->
source repaired
    ->
Factory Verification
    ->
Passed
```

The report must clearly distinguish:

```text
repair infrastructure exists
```

from:

```text
real Hermes performed the repair
```

---

# 10. Repair AgentDefinition

Verify that the live environment has the appropriate:

```text
Repair AgentDefinition
```

and that its configuration routes through the same Hermes/runtime infrastructure.

Do not silently fall back to an Implementation agent.

Do not create a special live-test-only execution path.

---

# 11. Review / Evaluation / Learning

Confirm that after successful verification the real pipeline invokes:

```text
WorkItemCompletionProcessor
```

and its current:

```text
Review
Evaluation
Learning
```

behavior.

Record whether each executes successfully.

Do not make live acceptance dependent on subjective review output unless the existing code already requires it.

If Evaluation or Learning fails under real execution but the current production policy intentionally allows completion, preserve that policy and report the observed behavior.

---

# 12. Project-Level Completion

After all WorkItems complete, the real system must automatically perform:

```text
Implementation
    ->
Testing
    ->
Reviewing
    ->
HumanReview
```

using:

```text
TaskSchedulerHostedService
ProjectCompletionGate
```

Do not manually transition these states.

The test must poll/wait using the repository's existing deterministic mechanism rather than arbitrary fixed sleeps wherever possible.

---

# 13. Human Approval

Use the real Project approval path:

```text
POST /api/projects/{id}/approve
```

or the actual current API contract.

Before approval:

```text
Project.Status == HumanReview
```

After approval:

```text
Project.Status == Completed
```

Also verify that:

```text
PATCH /api/projects/{id}/status
```

cannot bypass the HumanReview boundary.

---

# 14. End-to-End Evidence

The test/report must capture the following identifiers:

```text
ProjectId
WorkItemIds
AgentRunIds
OrchestrationRunId (when using compatibility API)
source repository/revision
workspace path
AgentRun statuses
WorkItem statuses
RepairAttemptCount
verification outcomes
Project status transitions
approval result
```

Do not expose secrets.

---

# 15. Failure Classification

If the live test fails, classify the failure precisely.

Use:

```text
A. Environment
B. Docker
C. Hermes startup
D. Provider/OmniRoute
E. AgentDefinition
F. Workspace mount
G. Agent instruction
H. Agent file modification
I. Structured result
J. WorkItem synchronization
K. Verification
L. Repair
M. Project completion gate
N. Human approval
```

Do not change code before establishing which category failed.

---

# 16. Fix Policy

Only fix issues required to make the production path correct.

Do NOT use:

- test-only bypasses;
- hard-coded Project IDs;
- hard-coded filesystem paths;
- fake success;
- manually forced statuses;
- disabling verification;
- disabling repair;
- widening permissions just to make Hermes work;
- bypassing Docker isolation.

Every fix must improve the actual production path.

---

# 17. Hermes Prompt Robustness

If the live test fails because the Agent cannot reliably perform the tiny deterministic coding task, inspect:

```text
AgentInstructionBuilder
Planner/Implementation instructions
Structured result contract
tool availability
workspace context
```

Fix the smallest abstraction necessary.

Do not solve unreliable LLM behavior by weakening Factory verification.

The rule remains:

> Agent output is untrusted. Factory verification is authoritative.

---

# 18. Provider / Model Configuration

Do not hard-code a model specifically for the E2E.

Use the repository's configured:

```text
Model
Provider
OmniRoute/OpenRouter routing
```

unless the existing Real Hermes test already defines an explicit E2E-safe model.

If a provider/model is unreliable, document the exact observation and use the existing configurable model mechanism.

Do not modify production defaults merely to make the test pass.

---

# 19. Docker Security

Do not weaken:

```text
--network none
```

or other existing container security controls.

Do not grant the container access to:

```text
host root
unrelated projects
credential directories
Docker socket
```

The only expected writable workspace is:

```text
Project.RootPath
```

where the existing architecture explicitly requires it.

---

# 20. Real Hermes Test Isolation

The live test must use:

- a unique temporary Project;
- a unique temporary workspace;
- a unique local Git fixture;
- isolated test data.

Do not let one live E2E run modify another Project.

Ensure cleanup occurs after the test.

Do not delete arbitrary global directories.

---

# 21. Timeouts

Use existing execution timeout configuration.

Do not introduce arbitrary long waits.

The live E2E should distinguish:

```text
Agent timeout
Verification timeout
Test infrastructure timeout
```

and report the corresponding existing status.

Do not increase global production timeouts simply to make this test pass.

---

# 22. Deterministic Test vs Live Test

Maintain two clearly different acceptance layers.

### Deterministic

```text
GoldenProjectLifecycleE2ETests
```

Uses controlled runtime/test infrastructure.

### Live

```text
RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled
```

Uses actual Hermes + Docker + provider.

Do not replace deterministic coverage with live tests.

Do not call the product "fully verified" merely because deterministic tests pass.

---

# 23. Manual Golden Path Verification

Also execute one manual full-path run using the actual running API/Worker stack.

Document the exact calls from the current repository.

Minimum manual flow:

```text
1. Start Docker
2. Start Hermes/provider infrastructure
3. Start API
4. Start Worker
5. Create Project
6. Initialize Git source
7. Verify source exists
8. Create WorkItems
9. Start/enable scheduler
10. Observe AgentRun
11. Observe Hermes container
12. Verify host source modification
13. Observe Verification
14. Observe Repair
15. Verify WorkItems Completed
16. Verify Project -> Testing
17. Verify Project -> Reviewing
18. Verify Project -> HumanReview
19. POST /api/projects/{id}/approve
20. Verify Project -> Completed
```

Use only endpoints and commands that actually exist.

---

# 24. Production-Path Hardening

During live testing, fix only concrete production-path issues discovered.

Possible examples:

- Docker volume/path mapping;
- provider environment propagation;
- Hermes process startup;
- result parsing;
- timeout handling;
- logging;
- retry/recovery;
- workspace permissions;
- AgentDefinition configuration.

Do not open unrelated architectural work.

---

# 25. Existing Tests

After live-path fixes run:

```bash
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
```

The entire deterministic suite must remain green.

Do not exclude tests to hide regressions.

---

# 26. Migration

Prefer zero migration.

A live-Hermes acceptance phase should not require database redesign.

If migration changes appear, report them separately and implement only when genuinely required by a production-path defect.

---

# 27. Validation

Run, in this order:

### A. Focused deterministic regression

Run the affected focused test classes.

### B. Golden deterministic E2E

Run:

```bash
dotnet test ... --filter "FullyQualifiedName~GoldenProjectLifecycle"
```

### C. Full deterministic suite

Run the complete solution test suite.

### D. Real Hermes

Run the exact live test:

```text
RealHermes_FullProjectLifecycle_WithRepair_WhenEnabled
```

with the repository's actual environment gate enabled.

### E. Manual full-path test

Execute one real project manually from API to Completed.

The report must clearly separate all five results.

---

# 28. GO / NO-GO

### GO requires:

1. Real Hermes actually executed.
2. Real Docker container actually executed.
3. Real initialized source was visible to Hermes.
4. Hermes modified the host Project.RootPath.
5. Factory Verification built/tested those actual changes.
6. At least one real verification failure entered Repairing.
7. Real repair execution restored the project.
8. WorkItems reached Completed through the normal pipeline.
9. Project automatically reached HumanReview.
10. Explicit approval produced Completed.
11. Deterministic suite remained green.
12. No security bypass was introduced.

### NO-GO if:

- Real Hermes did not actually run.
- Hermes ran only a Planner/no-WorkItem path.
- Workspace modification was not independently proven.
- Verification ran on another directory.
- Repair was only simulated.
- Project statuses were manually manipulated to obtain success.
- Provider failures were hidden.
- Docker isolation was weakened.
- deterministic tests were disabled/excluded to obtain green results.

---

# 29. Final Report

Create:

```text
.ai/context/phase-18.19-live-hermes-acceptance.md
```

Document:

1. Exact environment.
2. Docker version/status.
3. Hermes configuration.
4. Provider/router configuration without secrets.
5. Exact live test executed.
6. Whether Hermes really executed.
7. Project ID.
8. WorkItem IDs.
9. AgentRun IDs.
10. Workspace path.
11. Source revision.
12. Actual Agent modification.
13. Verification result.
14. Repair result.
15. Review/Evaluation/Learning result.
16. Project lifecycle transitions.
17. Human approval result.
18. Deterministic test results.
19. Full test results.
20. Manual E2E result.
21. Issues discovered.
22. Production-path fixes made.
23. Remaining limitations.
24. Final GO/NO-GO decision.

---

# 30. Definition of Done

This phase is VERIFIED only when there is actual evidence for:

```text
REAL PROJECT
     |
     v
REAL GIT SOURCE
     |
     v
REAL WORKSPACE
     |
     v
REAL WORKITEM
     |
     v
REAL AGENTRUN
     |
     v
REAL HERMES
     |
     v
REAL DOCKER CONTAINER
     |
     v
REAL SOURCE MODIFICATION
     |
     v
REAL FACTORY VERIFICATION
     |
     +------ PASS ------+
     |                  |
     |                 FAIL
     |                  |
     |                  v
     |               REPAIR
     |                  |
     |                  v
     |              REAL HERMES
     |                  |
     |                  v
     |             VERIFICATION
     |                  |
     +------------------+
                |
                v
        WORKITEM COMPLETED
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

The report must distinguish:

```text
DETERMINISTIC E2E: PASSED
REAL HERMES E2E: PASSED
MANUAL GOLDEN PATH: PASSED
```

Only when these are actually observed may the phase receive GO.

If Docker/provider/Hermes is unavailable, the correct result is **NOT VERIFIED**, not GO.

Final decision must be exactly:

```text
Live Hermes Acceptance: VERIFIED
Final Decision: GO
```

or:

```text
Live Hermes Acceptance: NOT VERIFIED
Final Decision: NO-GO
```

Core principle:

> Phase 18.18 proved that the architecture can work.
> Phase 18.19 must prove that the actual product works.
> A compiled test is not a live test.
> A successful Hermes response is not verification.
> A fake repair is not a real repair.
> The final evidence must come from a real Project, real source, real Hermes execution, real filesystem modification, real Factory verification, and real Project completion.