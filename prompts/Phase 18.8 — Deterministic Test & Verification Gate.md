# Phase 18.8 — Deterministic Test & Verification Gate

## 1. Phase Objective

Build the next missing production loop:

```text
Completed-by-Agent WorkItem
        |
        v
Deterministic Verification/Test Gate
        |
        +--> verification passed --> WorkItem.Completed
        |
        +--> verification failed --> WorkItem.Repairing / appropriate existing state
        |
        +--> verification infrastructure/error --> deterministic failure state
```

Phase 18.7 established that an AgentRun saying `"completed"` is sufficient to move a WorkItem to `Completed` only after the structured execution result is valid.

However, Phase 18.7 explicitly leaves a known limitation:

- `WorkItem.Testing` and `WorkItem.Reviewing` are not yet part of the real execution flow.
- `artifacts`, `tests`, and `notes` are currently parsed but not persisted/interpreted.
- A real LLM can over-claim `"completed"`.

This phase must close the most important part of that gap:

> **A WorkItem must have an authoritative, deterministic verification gate before the Factory considers its implementation genuinely verified.**

The verification gate must execute deterministic repository/project checks rather than trusting the LLM's claim.

---

# 2. Mandatory Repository Inspection

Before changing anything, inspect:

- `WorkItem`
- `WorkItemStatus`
- `WorkItemTransitions`
- `Project`
- `ProjectStatus`
- `ProjectTransitions`
- `AgentRun`
- `AgentRunStatus`
- `AgentRunTransitions`
- `TaskDispatchScheduler`
- `TaskReadinessService`
- `TaskStateManager`
- `AgentRunWorker`
- `AgentRunWorkItemSync`
- `IWorkItemExecutionResultMapper`
- `WorkItemExecutionResultMapper`
- `StructuredAgentResult`
- `IAgentOrchestrator`
- `IAgentRuntime`
- existing process/terminal execution abstractions
- existing test/build execution infrastructure
- any existing Docker/sandbox/process runner
- existing configuration for execution
- existing API endpoints for WorkItems/AgentRuns
- existing CI validation commands
- existing tests related to WorkItem lifecycle
- `AGENTS.md`
- roadmap/context files related to Testing, Verification, Repair, Review

Also inspect whether the repository already has a reusable abstraction for:

```text
build
test
command execution
process execution
workspace validation
```

**Do not build a second process runner if one already exists.**

The implementation must reuse existing infrastructure wherever possible.

---

# 3. Exact Architectural Goal

The target architecture is:

```text
TaskDispatchScheduler
        |
        v
Implementation AgentRun
        |
        v
AgentRunWorker
        |
        v
AgentRunWorkItemSync
        |
        v
WorkItem enters verification state
        |
        v
Verification/Test Gate
        |
        v
Deterministic command execution
        |
        v
Verification Result
        |
        +----------------------+
        |                      |
        v                      v
     PASSED                 FAILED
        |                      |
        v                      v
 WorkItem.Completed      Repairable state
```

Do not introduce another AgentRun mechanism merely to execute deterministic tests.

If the existing architecture supports representing verification as a WorkItem/AgentRun, determine whether that is already intended by the domain before adding anything.

The preferred design is to keep:

- deterministic test execution separate from LLM execution
- Factory verification authoritative
- shell/process execution behind an explicit abstraction
- results structured and persisted where useful
- WorkItem lifecycle transitions explicit

---

# 4. Critical Design Rule

**Do not trust the LLM's `"completed"` result as proof of correctness.**

The LLM can report:

```json
{
  "execution": {
    "status": "completed"
  }
}
```

but this only means:

> the agent claims it completed the implementation.

It must NOT automatically mean:

> the implementation passed verification.

The Factory must independently execute the applicable deterministic verification commands.

---

# 5. Verification Contract

Introduce a minimal contract if no equivalent exists:

```text
IVerificationGate
VerificationGate
```

or use an existing abstraction discovered during repository inspection.

The contract should conceptually provide:

```csharp
Task<VerificationResult> VerifyAsync(
    Project project,
    WorkItem workItem,
    CancellationToken cancellationToken);
```

The exact API should follow repository conventions.

The result should distinguish at least:

```text
Passed
Failed
InfrastructureError
TimedOut
Cancelled
```

Do not add categories that the existing domain does not need.

The result must contain enough deterministic information for diagnostics, such as:

- command(s) executed
- exit code
- duration
- stdout/stderr summary
- failure reason

Avoid storing unlimited process output.

---

# 6. Verification Command Resolution

The system must determine **what to verify** without asking the LLM to invent arbitrary shell commands.

The first implementation should use deterministic configuration or repository/project conventions.

Examples:

```text
dotnet build
dotnet test
```

or existing project-specific verification commands already present in the repository.

The exact commands must be discovered from the repository.

Do NOT let the LLM return:

```text
"run: rm -rf ..."
```

or any arbitrary command and then execute it automatically.

The Factory owns the verification command policy.

If a project does not yet have a deterministic verification configuration, define the smallest safe mechanism required by the current repository rather than implementing a generic command DSL.

---

# 7. Security Boundary

Any process execution introduced by this phase must be explicitly controlled.

Do not execute arbitrary LLM-generated shell commands.

Verification commands must originate from:

1. trusted application configuration, or
2. trusted repository/project configuration that is explicitly supported by the Factory.

Do not allow:

- arbitrary command injection through WorkItem.Description
- arbitrary executable paths supplied by the LLM
- unrestricted host filesystem access
- arbitrary Docker commands from model output
- credential/environment leakage

Reuse existing workspace/process isolation if already available.

If the current repository does not have safe process isolation, keep the implementation limited to the existing trusted execution mechanism and document the boundary.

---

# 8. WorkItem Lifecycle

Use the existing `WorkItemTransitions`.

Do NOT modify the lifecycle merely because a more elaborate state machine would be aesthetically preferable.

The intended semantics are:

```text
Implementation agent reports completed
        |
        v
WorkItem enters Testing
        |
        v
VerificationGate
        |
        +--> pass --> Completed
        |
        +--> fail --> Repairing or existing recoverable state
```

However:

**Inspect the actual transition table first.**

If the current state machine does not permit:

```text
Completed -> Testing
```

or:

```text
Running -> Testing
Testing -> Completed / Repairing
```

then add only the minimum required transitions.

Do not redesign the entire state machine.

---

# 9. Important Dependency Semantics

Phase 18.7 established that:

```text
ITaskDependencyResolver
```

currently treats only:

```text
WorkItemStatus.Completed
```

as satisfying a dependency.

Therefore:

```text
Testing
```

must NOT satisfy dependencies.

A WorkItem that is being tested must remain non-ready for dependents.

Only after verification passes and the WorkItem reaches:

```text
Completed
```

may dependent WorkItems become dispatchable.

Add an integration test proving this.

---

# 10. Verification Trigger

Determine the cleanest trigger based on the existing Worker architecture.

Preferred flow:

```text
AgentRun Succeeded
+ execution result = completed
        |
        v
WorkItem Testing
        |
        v
Verification Gate
```

The verification gate must NOT execute inside the HTTP request.

It must execute asynchronously through the existing Worker/background-processing architecture.

Do not introduce an independent hosted service if the current Worker can safely own the lifecycle.

Avoid creating a second polling loop.

---

# 11. Result Persistence

Before introducing a migration, determine whether the existing model can represent verification state/results.

Prefer zero migration if possible.

If persistence is genuinely required, use a minimal additive model.

Potential information:

```text
VerificationStatus
VerificationSummary
VerifiedAtUtc
VerificationAttempt
```

But do not add all of these automatically.

Only persist information that is required by:

- lifecycle correctness
- observability
- recovery
- future repair flow

If no persistence is necessary, keep the result ephemeral and use existing `CurrentError`/status mechanisms.

If a migration is required, document exactly why.

---

# 12. Idempotency and Concurrency

Verification must be safe against duplicate Worker observations.

For a WorkItem already in:

```text
Testing
```

only one verification operation should own the transition.

Reuse existing:

- `Version`
- optimistic concurrency
- Worker claim mechanisms
- existing lifecycle guards

Do NOT introduce distributed locks.

Do NOT use static in-memory ownership.

A duplicate verification attempt must not:

- run indefinitely in parallel
- regress `Completed`
- overwrite a newer result
- transition a WorkItem twice
- resurrect a failed/cancelled WorkItem

If the existing architecture cannot safely guarantee this, identify the smallest missing concurrency guard and implement only that.

---

# 13. Verification Failure Policy

A failed deterministic test/build is NOT automatically a permanent WorkItem failure.

The intended distinction is:

```text
Verification Passed
    -> Completed

Verification Failed
    -> Repairable state

Verification infrastructure failed
    -> deterministic infrastructure/error handling

Verification timed out
    -> deterministic timeout handling
```

Use the actual existing `WorkItemTransitions`.

If:

```text
Testing -> Repairing
```

already exists, use it.

If it does not, determine whether the smallest correct transition should be added.

Do not invent an autonomous repair loop in this phase.

A repairable result should simply make the state available for the future repair phase.

---

# 14. No Automatic Repair Loop

This phase must NOT implement:

```text
Testing
 -> Repair Agent
 -> Testing
 -> Repair Agent
 -> ...
```

It must only establish:

```text
Implementation
 -> Verification
 -> Pass / Repairable failure
```

The next phase can consume a repairable WorkItem.

Do not add:

- retry-until-green
- automatic LLM repair
- unlimited attempts
- autonomous loops

---

# 15. Verification Result Contract

Create a small deterministic result model if none exists.

Conceptually:

```text
VerificationResult
    Status
    Summary
    ExitCode?
    Duration
```

Do not persist raw stdout/stderr indefinitely.

Use bounded diagnostic output.

A verification result must be deterministic for identical:

```text
workspace state
+
verification configuration
```

The Factory must never infer success from textual output such as:

```text
"looks good"
```

The primary success signal must be the trusted process result/exit status.

---

# 16. Tests

Add focused tests only.

### Unit tests

For verification-result interpretation:

- exit code 0 -> Passed
- non-zero exit -> Failed
- timeout -> TimedOut
- cancellation -> Cancelled
- process/infrastructure failure -> InfrastructureError
- bounded output handling
- deterministic result mapping

### Application integration tests

Use real SQLite and real domain services.

Prove:

1. Agent reports `"completed"`.
2. WorkItem enters Testing.
3. Verification gate executes.
4. Successful verification moves WorkItem to Completed.
5. Failed verification moves WorkItem to the appropriate recoverable state.
6. Failed verification does not satisfy dependencies.
7. Successful verification does satisfy dependencies.
8. Duplicate processing is safe.
9. Cancelled/failed WorkItems are not resurrected.
10. Verification failure does not create duplicate AgentRuns.

Use a deterministic fake process runner for most tests.

Do not mock the entire domain orchestration.

---

# 17. Real Repository Verification

Because this phase is specifically about deterministic verification, add at least one test using the actual repository's trusted verification command abstraction if one exists.

The test should prove that:

```text
trusted command
 -> process runner
 -> exit code
 -> VerificationResult
```

works correctly.

Do not require an actual production build/test in every unit test.

---

# 18. Real Hermes E2E

Do NOT make the real Hermes E2E the primary proof of this phase.

The important proof is:

```text
LLM result
 -> Testing
 -> deterministic verification
 -> final WorkItem state
```

If the existing real Hermes suite is available, add at most one focused opt-in test:

```text
AI_FACTORY_HERMES_SMOKE_TEST=true
```

The real test should prove:

```text
real Implementation Agent
 -> structured completed result
 -> Testing
 -> real deterministic verification
 -> final state
```

Use the existing anti-fabrication marker convention if necessary.

If not run:

```text
Real WorkItem Verification E2E: NOT RUN
```

Never claim it passed if it was not executed.

---

# 19. API Scope

Do not add a synchronous verification endpoint.

Do not execute build/test commands from HTTP.

If observability is missing, prefer exposing existing WorkItem/AgentRun status rather than creating a new API surface.

Only add an endpoint if a concrete operational need is discovered.

---

# 20. Scheduler Scope

Do not modify the scheduler unless the verification lifecycle exposes a proven scheduler bug.

The scheduler remains responsible for:

```text
Ready
 -> Running
 -> AgentRun dispatch
```

Verification remains responsible for:

```text
Testing
 -> Completed / Repairable
```

The scheduler should naturally rediscover dependents only after the verified WorkItem becomes `Completed`.

---

# 21. Recovery Considerations

Inspect Phase 18.4 recovery behavior.

A WorkItem stuck in:

```text
Testing
```

must not be accidentally treated as an AgentRun-stale implementation task.

Do not redesign recovery.

If verification itself needs recovery semantics, identify the smallest required mechanism and document it.

Do not create another generic recovery framework.

---

# 22. Explicit Non-Goals

Do NOT implement:

- autonomous repair agent
- repair loop
- code review agent
- security review agent
- multi-agent orchestration
- OpenViking
- learning/RL
- new model-selection system
- new Hermes runtime
- new scheduler
- Planner changes
- ProjectPlanService changes
- PlannerResultMapper changes
- new dependency resolver
- arbitrary shell-command execution from LLM output
- UI
- Git workflow redesign
- CI/CD redesign
- repository-wide indexing
- vector database
- generic workflow engine

---

# 23. Migration Policy

Prefer:

```text
NO MIGRATION
```

If a schema change is required, justify it explicitly before implementation.

After changes run:

```text
dotnet ef migrations has-pending-model-changes --project ../AI.Factory.Infrastructure --startup-project .
```

Do not create a migration merely to store information that can safely remain in existing fields.

---

# 24. Empirical Verification Gates

Run:

```text
dotnet build AI.Factory.slnx -c Debug
dotnet build AI.Factory.slnx -c Release
dotnet test AI.Factory.slnx -c Debug --no-build
dotnet ef migrations has-pending-model-changes --project ../AI.Factory.Infrastructure --startup-project .
```

Also run focused verification tests separately.

Report exact counts.

If real Hermes is not enabled:

```text
Real WorkItem Verification E2E: NOT RUN
```

Do not fabricate execution evidence.

---

# 25. Definition of Done

Phase 18.8 is **GO** only when:

- [ ] Repository verification/process infrastructure was inspected first.
- [ ] Existing process execution infrastructure is reused where possible.
- [ ] LLM `"completed"` is no longer treated as sufficient proof of correctness.
- [ ] WorkItem verification is deterministic.
- [ ] Verification commands are trusted/configured, not LLM-invented.
- [ ] WorkItem enters Testing before final verification where compatible with the existing lifecycle.
- [ ] Testing does not satisfy dependencies.
- [ ] Only verified Completed WorkItems satisfy dependencies.
- [ ] Verification failure produces a recoverable state where the existing lifecycle supports it.
- [ ] No autonomous repair loop was introduced.
- [ ] Duplicate verification processing is safe.
- [ ] Cancelled/failed WorkItems cannot be resurrected.
- [ ] No unnecessary migration was introduced.
- [ ] Debug build passes.
- [ ] Release build passes.
- [ ] Full test suite passes.
- [ ] Focused verification tests pass.
- [ ] Real Hermes E2E is honestly reported.
- [ ] No Phase 18.1–18.7 subsystem was unnecessarily rewritten.
- [ ] No commit/push is performed.

---

# 26. Required Final Report

Create:

```text
.ai/context/phase-18.8-test-verification-gate.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Existing Verification/Process Infrastructure
4. Exact Gap Identified
5. Architecture After Phase 18.8
6. Verification Contract
7. Trusted Command Resolution
8. Security Boundary
9. WorkItem Lifecycle Policy
10. Dependency Semantics
11. Idempotency / Concurrency
12. Tests Added
13. Exact Verification Results
14. Real Hermes E2E Result
15. Migration Status
16. Known Limitations
17. Remaining Risks
18. Final Decision

Clearly distinguish:

```text
VERIFIED
NOT RUN
KNOWN LIMITATION
RISK
```

---

# 27. Final Decision

End the report with exactly one of:

```text
WorkItem Test & Verification Gate: VERIFIED
Final Decision: GO
```

or:

```text
WorkItem Test & Verification Gate: NOT VERIFIED
Final Decision: NO-GO
```

A GO decision requires empirical evidence.

Do not commit or push anything.