# Phase 18.3 — Deterministic Task Scheduler & Dispatch Engine

## Phase Objective

Build the deterministic Task Scheduler and Dispatch Engine that consumes the existing Project/WorkItem dependency and readiness infrastructure and decides which executable WorkItem should be dispatched next.

Phase 18.2 is VERIFIED and must be treated as the stable baseline.

The existing system already has:

- Project domain and lifecycle
- WorkItem domain and lifecycle
- WorkItemDependency
- TaskDependencyResolver
- TaskReadinessService
- TaskScheduler / TaskStateManager legacy infrastructure
- AgentRun.ProjectId
- AgentRun.WorkItemId
- AgentRun lifecycle
- AgentWorker
- AgentOrchestrator
- Hermes runtime
- Structured AgentExecutionInstruction
- Structured AgentResult
- real Hermes + OmniRoute execution

Do NOT rebuild these components.

The objective of this phase is to connect deterministic task readiness to controlled task dispatch.

---

# 1. Repository Inspection

Before modifying anything:

1. Read `AGENTS.md`.
2. Read:
   `.ai/context/phase-18.2-project-task-domain.md`
3. Inspect the complete existing implementation of:
   - `TaskReadinessService`
   - `ITaskDependencyResolver`
   - `TaskDependencyResolver`
   - `TaskScheduler`
   - `TaskStateManager`
   - `WorkItem`
   - `WorkItemTransitions`
   - `WorkItemDependency`
   - `AgentRun`
   - `AgentRunService`
   - `AgentRunRepository`
   - `AgentWorker`
   - `AgentOrchestrator`
4. Determine whether the pre-existing `TaskScheduler` is sufficient to evolve or should be replaced/refactored.
5. Search all usages before changing any existing scheduler/state-manager API.

Do not create a duplicate scheduler if an existing abstraction can be extended safely.

---

# 2. Core Scheduler Responsibility

Introduce or complete a deterministic scheduler responsible for:

1. discovering executable WorkItems
2. evaluating readiness
3. selecting eligible WorkItems
4. respecting priority/order
5. enforcing concurrency limits
6. dispatching selected WorkItems
7. preventing duplicate dispatch
8. handling races safely
9. remaining independent from LLM reasoning

The scheduler must never ask an LLM:

> "Which task should I run next?"

That decision must be deterministic.

---

# 3. Scheduler Contract

Create an application-level scheduler abstraction following repository conventions.

Conceptually:

`ITaskScheduler`

with capabilities equivalent to:

- find ready tasks
- select next task(s)
- dispatch ready task
- run one scheduling cycle

Use the repository's existing interfaces where possible.

Do not expose EF entities from the scheduler contract.

The scheduler must return enough information for the caller to know:

- selected WorkItem
- Project
- readiness
- dispatch result
- associated AgentRun where one was created

Use explicit result types rather than ambiguous booleans where useful.

---

# 4. Deterministic Selection Rules

Define and document one deterministic ordering policy.

The ordering must be stable and reproducible.

At minimum consider:

1. WorkItem readiness
2. WorkItem priority
3. creation time
4. stable WorkItem ID as final tie-breaker

Use existing priority semantics if already defined.

Do not invent an LLM-based ranking mechanism.

The same database state must produce the same selected task.

---

# 5. Readiness Integration

The scheduler must consume the existing readiness infrastructure.

Do not duplicate dependency traversal logic.

The authoritative chain must remain:

```text
WorkItem
   ↓
TaskDependencyResolver
   ↓
TaskReadinessService
   ↓
Scheduler
```

Do not create a second DFS/cycle-detection algorithm.

A task is eligible only when the existing readiness semantics classify it as executable.

At minimum:

- no dependencies → Ready
- all dependencies Completed → Ready
- pending/running/blocked dependency → Waiting
- failed dependency → BlockedByFailedDependency
- own task already Running/Completed/Failed/Cancelled → not dispatchable

---

# 6. Dispatch Model

A scheduler decision must not directly execute Hermes.

The scheduler should create/prepare the appropriate AgentRun and let the existing AgentWorker/AgentOrchestrator execution path perform the actual agent execution.

Preferred flow:

```text
Scheduler
   │
   ├── select Ready WorkItem
   │
   ├── atomically claim/mark task
   │
   ├── create AgentRun
   │
   └── enqueue/leave run for existing Worker
                 │
                 ▼
            AgentWorker
                 │
                 ▼
           AgentOrchestrator
                 │
                 ▼
              Hermes
```

The scheduler must not bypass:

- AgentRun
- AgentWorker
- AgentOrchestrator
- IAgentRuntime

---

# 7. Atomicity / Race Safety

This is a critical part of the phase.

Assume multiple scheduler cycles or multiple Worker/host instances may run concurrently.

The same WorkItem must not result in multiple active AgentRuns because two schedulers observed `Ready` simultaneously.

Use the existing optimistic concurrency/database mechanisms where appropriate.

Possible mechanisms include:

- WorkItem version/concurrency token
- atomic status transition
- unique active-run constraint if compatible with existing schema
- transactional claim
- existing AgentRun correlation/idempotency mechanism

Choose the simplest mechanism consistent with the current architecture.

Do NOT solve races using in-memory locks only.

The protection must work across multiple processes/containers.

---

# 8. AgentRun Creation

When a WorkItem is dispatched:

- create an AgentRun linked to the Project
- link it to the WorkItem
- use the existing AgentDefinition mechanism
- preserve correlation/idempotency semantics
- leave the run in the state expected by the existing AgentWorker
- do not execute Hermes directly

Do not change AgentRun lifecycle semantics unless a concrete scheduler race requires a minimal change.

If an AgentRun already exists for the same WorkItem and is active/pending, the scheduler must not create another active execution.

---

# 9. WorkItem State Transition

Define the exact transition when a WorkItem is claimed for execution.

Use the existing `WorkItemTransitions`.

Do not mutate `WorkItem.Status` directly.

The scheduler should use the domain transition API.

Expected conceptual transition:

```text
Ready
  ↓
Running
```

The scheduler must not mark a task `Completed` merely because an AgentRun was created.

Completion belongs to execution/result handling.

---

# 10. AgentRun ↔ WorkItem Completion

Inspect the current architecture and determine where the existing AgentRun completion can be mapped back to WorkItem.

If this mechanism does not yet exist, implement only the minimum required application service/handler to synchronize:

```text
AgentRun result
      ↓
WorkItem state
```

The mapping must distinguish:

- successful AgentRun
- failed AgentRun
- cancelled AgentRun
- timed-out AgentRun

Do not introduce Planner/Coder/Tester-specific behavior.

For this phase, synchronization should be generic.

If an AgentRun succeeds:

```text
WorkItem → Completed
```

If it fails:

```text
WorkItem → Failed
```

If it is cancelled:

```text
WorkItem → Cancelled
```

If it times out:

use the existing domain semantics and document the chosen state.

Do not silently overwrite a WorkItem that has already transitioned independently.

---

# 11. Concurrency Limits

Introduce configurable scheduler concurrency.

The scheduler must support a deterministic maximum number of simultaneously dispatched WorkItems.

Use configuration rather than hard-coded values.

For example conceptually:

```text
TaskSchedulerOptions
    MaxConcurrentTasks
    SchedulingInterval
```

Use repository/configuration conventions.

Do not create a second independent Worker process architecture.

The scheduler controls dispatch capacity; the existing AgentWorker remains responsible for executing AgentRuns.

---

# 12. Scheduling Cycle

Implement a deterministic scheduling cycle.

Conceptually:

```text
Start cycle
   ↓
Find candidate WorkItems
   ↓
Evaluate readiness
   ↓
Sort deterministically
   ↓
Apply concurrency limit
   ↓
Claim candidates atomically
   ↓
Create AgentRuns
   ↓
Finish cycle
```

The cycle must be safe to execute repeatedly.

Running the scheduler twice against unchanged state must not create duplicate active executions.

---

# 13. Failure Handling

If dispatch of one WorkItem fails:

- do not corrupt other WorkItems
- do not terminate the scheduler
- preserve deterministic state
- record the failure through existing logging/metrics conventions

The scheduler must continue processing other eligible tasks where safe.

Do not swallow exceptions silently.

---

# 14. Recovery

The scheduler must recover from stale/incomplete states where possible.

Inspect existing Worker recovery behavior.

Do not introduce an independent recovery system if the existing AgentRun Worker already owns execution recovery.

If a WorkItem is `Running` but has no active AgentRun, define and test the behavior explicitly.

Do not guess.

Document the selected recovery policy.

---

# 15. API / Operations

Expose only the minimum operational visibility needed.

A scheduler status endpoint may expose:

- scheduler enabled/disabled
- configured concurrency
- current active dispatch count
- last scheduling cycle
- last cycle result
- selected/dispatch count
- failure count

Do not build a dashboard.

Do not create manual "execute arbitrary task" endpoints unless required by the existing architecture.

---

# 16. Metrics

Add only useful scheduler metrics.

At minimum consider:

- scheduling cycles
- ready tasks discovered
- tasks dispatched
- dispatch failures
- tasks skipped because of concurrency
- duplicate/race prevention events

Follow existing `IOperationalMetrics` conventions.

Do not redesign metrics.

---

# 17. Tests

This phase must have strong deterministic concurrency tests.

### Selection

Test:

- one Ready task
- multiple Ready tasks
- priority ordering
- creation-time ordering
- stable ID tie-breaker
- blocked tasks ignored
- waiting tasks ignored
- completed/running tasks ignored

### Dependency behavior

Test:

- no dependencies → dispatchable
- completed dependencies → dispatchable
- pending dependency → not dispatchable
- running dependency → not dispatchable
- failed dependency → not dispatchable
- circular graph remains non-dispatchable

### Dispatch

Test:

- Ready WorkItem → AgentRun created
- AgentRun linked to Project
- AgentRun linked to WorkItem
- WorkItem becomes Running
- scheduler does not execute Hermes directly

### Idempotency

Run scheduling cycle twice.

Verify:

- only one active AgentRun exists
- no duplicate execution
- WorkItem remains correctly Running

### Concurrency

Create more Ready tasks than the configured concurrency.

Verify:

- only allowed number are dispatched
- deterministic ordering is preserved
- remaining tasks remain Ready

### Race condition

Simulate two concurrent scheduler cycles/processes attempting to dispatch the same WorkItems.

Verify:

- no duplicate active AgentRuns
- no invalid WorkItem transition
- one winner
- loser safely skips/retries

Do not use only an in-memory lock to make this test pass.

### Completion

Test generic synchronization:

- AgentRun success → WorkItem Completed
- AgentRun failure → WorkItem Failed
- AgentRun cancellation → WorkItem Cancelled
- timeout according to documented policy

### Failure isolation

One dispatch failure must not prevent other eligible WorkItems from being processed.

---

# 18. Real Hermes

Do not make real Hermes execution the main validation mechanism of this phase.

The scheduler itself should be tested deterministically.

However, because the scheduler now creates real AgentRuns, add ONE opt-in real-Hermes integration test only if the existing test infrastructure can execute it cleanly:

```text
Ready WorkItem
→ Scheduler
→ AgentRun
→ Worker
→ Orchestrator
→ Hermes
→ OmniRoute
→ real LLM
→ result
→ WorkItem Completed
```

Use the existing Phase 18.1.8/18.1.7 infrastructure.

Do not redesign or duplicate the real-Hermes harness.

If the scheduler does not yet have enough integration infrastructure for this test, document it as a limitation rather than forcing unrelated architecture into this phase.

---

# 19. Persistence / Migration

Inspect whether the current model already has all required concurrency fields.

Prefer existing fields.

Only create an EF migration if genuinely required.

If a migration is required:

1. inspect generated migration
2. verify no destructive operations
3. test clean migration
4. test upgrade from latest schema
5. verify existing AgentRun/Project/WorkItem data preservation

Do not introduce a new database.

---

# 20. Explicit Non-Goals

Do NOT implement:

- Planner Agent
- LLM-based planning
- automatic task decomposition
- Specification Agent
- Architecture Agent
- Coding Agent
- Testing Agent
- Reviewer Agent
- Repair Agent
- OpenViking
- multi-agent orchestration
- Git automation
- autonomous commits
- deployment
- learning/experience system
- UI/dashboard

The scheduler must remain generic.

---

# 21. Important Architectural Boundary

The final architecture after this phase should be:

```text
                 Project
                    │
                 WorkItems
                    │
             Dependencies
                    │
             Readiness Query
                    │
                Scheduler
                    │
              create/claim
                    │
                 AgentRun
                    │
                 Worker
                    │
             Orchestrator
                    │
             IAgentRuntime
                    │
                 Hermes
                    │
              OmniRoute/LLM
```

The scheduler decides **WHEN** a WorkItem is eligible.

The future Planner will decide **WHAT WorkItems should exist**.

Hermes decides **HOW an Agent performs its assigned work**.

Keep these responsibilities separate.

---

# 22. Validation Gate

Run:

1. targeted scheduler tests
2. dependency/readiness regression
3. AgentRun linkage tests
4. API tests if API changed
5. concurrency/race tests
6. full deterministic regression
7. Debug build
8. Release build
9. migration validation if applicable
10. optional real-Hermes scheduler E2E if implemented

Do not repeat the complete Phase 18.1.7 stability suite unless AgentRun/Hermes execution behavior is materially modified.

Never fabricate evidence.

---

# 23. Documentation

Create:

`.ai/context/phase-18.3-task-scheduler.md`

Document:

- scheduler architecture
- deterministic ordering rules
- readiness integration
- concurrency policy
- atomic claim strategy
- AgentRun dispatch behavior
- WorkItem completion mapping
- recovery behavior
- metrics
- API changes
- persistence/migrations
- concurrency test evidence
- real-Hermes evidence if run
- known limitations
- next-phase recommendation

---

# 24. Definition of Done

Phase is GO only if:

- scheduler abstraction exists
- scheduler consumes existing readiness logic
- no duplicate dependency algorithm exists
- deterministic task ordering is implemented
- concurrency limit is enforced
- WorkItem claiming is race-safe across processes
- duplicate active AgentRuns are prevented
- AgentRuns are linked correctly to Project/WorkItem
- scheduler never directly invokes Hermes
- WorkItem transitions use the existing state machine
- generic AgentRun completion maps correctly to WorkItem state
- scheduler failures are isolated
- scheduling cycles are safely repeatable
- deterministic concurrency/race tests pass
- existing AgentRun/Hermes behavior remains compatible
- full regression passes
- Debug build has 0 warnings/errors
- Release build has 0 warnings/errors
- no unnecessary future-agent architecture was introduced

Final report MUST explicitly state:

`Task Scheduler: VERIFIED / NOT VERIFIED`

and

`Final Decision: GO / NO-GO`

Do not commit or push unless explicitly instructed.

Do not claim evidence that was not actually executed.