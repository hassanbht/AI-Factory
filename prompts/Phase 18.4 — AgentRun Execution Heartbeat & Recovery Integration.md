# Phase 18.4 — AgentRun Execution Heartbeat & Recovery Integration

## Phase Objective

Resolve the concrete recovery limitation discovered and verified in Phase 18.3:

> WorkItems dispatched through the new `AgentRun → AgentRunWorker → AgentOrchestrator → IAgentRuntime` pipeline currently do not update the existing task heartbeat. The existing stale-execution recovery therefore relies on `StartedAtUtc` and can incorrectly recover a genuinely running AgentRun-dispatched WorkItem.

The goal of this phase is to make the existing recovery mechanism correctly recognize active AgentRun-backed WorkItems without introducing a second independent recovery system.

The implementation must preserve the existing architecture:

```text
TaskDispatchScheduler
    ↓
WorkItem Running
    ↓
AgentRun Queued
    ↓
AgentRunWorker
    ↓
AgentOrchestrator
    ↓
IAgentRuntime / Hermes
    ↓
heartbeat while execution is genuinely active
    ↓
existing WorkflowRecoveryHostedService
```

Do NOT redesign the recovery subsystem.

---

# 1. Repository Inspection

Before changing code:

1. Read `AGENTS.md`.
2. Inspect the complete current implementation of:
   - `AgentRun`
   - `AgentRunWorker`
   - `AgentOrchestrator`
   - `IAgentRuntime`
   - `HermesAgentRuntime`
   - `IAgentExecutor`
   - `AgentRunWorkItemSync`
   - `ITaskStateManager`
   - `TaskStateManager`
   - `WorkflowRecoveryHostedService`
   - `RecoveryOptions`
   - `WorkItem`
   - `WorkItemTransitions`
   - `TaskDispatchScheduler`
3. Inspect existing heartbeat-related tests and recovery tests.
4. Inspect how `StartedAtUtc`, `LastHeartbeatAtUtc`, `CurrentError`, `Status`, and `Version` are currently persisted.
5. Inspect DI registration in both API and Worker.
6. Read the Phase 18.3 report:
   `.ai/context/phase-18.3-task-scheduler.md`
7. Do not assume the Phase 18.3 report's recommendation is still exactly applicable; verify the current code first.

---

# 2. Scope

Implement a reliable heartbeat/staleness signal for WorkItems executing through the AgentRun pipeline.

The solution must integrate with the existing recovery infrastructure.

The implementation must support:

- AgentRun-backed WorkItem execution
- long-running AgentRun execution
- periodic heartbeat updates while execution is active
- graceful completion
- failure
- cancellation
- timeout
- worker shutdown
- stale execution recovery
- optimistic concurrency
- multiple Worker processes/instances

---

# 3. Architecture Constraint

Do NOT create a new independent recovery system.

Do NOT replace:

- `WorkflowRecoveryHostedService`
- `RecoveryOptions`
- `ITaskStateManager`
- existing stale execution semantics

Instead, make the existing recovery mechanism aware of active AgentRun-backed execution through the existing heartbeat/staleness mechanism, or introduce the smallest equivalent abstraction only if the current design makes that strictly necessary.

The existing recovery service must remain the authority for stale WorkItem recovery.

---

# 4. Heartbeat Design

Determine the safest implementation based on the existing code.

The preferred approach is:

```text
AgentRunWorker
    ↓
AgentOrchestrator execution
    ↓
periodic heartbeat while ExecuteAsync is active
    ↓
ITaskStateManager.HeartbeatAsync(workItemId)
```

However, do not blindly implement this exact shape.

If heartbeat ownership belongs more appropriately in `AgentOrchestrator`, `AgentRunWorker`, or a dedicated execution-scope component, choose the design that:

1. guarantees heartbeat during real execution,
2. does not require Hermes-specific code,
3. works for every `IAgentRuntime`,
4. does not change `IAgentRuntime` unnecessarily,
5. does not leak background tasks,
6. stops heartbeat after execution ends,
7. behaves correctly during cancellation and timeout.

Document the chosen ownership model.

---

# 5. Heartbeat Interval

Do not hard-code a heartbeat interval if the existing recovery configuration provides an appropriate value.

Inspect `RecoveryOptions`.

If necessary, introduce a small explicit configuration such as:

```text
AgentRunHeartbeatInterval
```

Use a sensible default derived from the existing stale timeout.

The relationship must make sense:

```text
heartbeat interval << stale execution timeout
```

The implementation must prevent a healthy long-running execution from becoming stale merely because the execution duration exceeds the stale timeout.

Do not use extremely aggressive polling.

---

# 6. Heartbeat Lifecycle

For an AgentRun associated with a WorkItem:

### Before execution

The WorkItem must already be `Running`.

Do not bypass `WorkItemTransitions`.

### During execution

Periodically update:

```text
WorkItem.LastHeartbeatAtUtc
```

through the existing task-state abstraction where possible.

Do not directly mutate the database from the heartbeat loop.

### On successful execution

Normal AgentRun completion and existing:

```text
AgentRunWorkItemSync
```

must remain responsible for terminal WorkItem synchronization.

Do not make heartbeat responsible for completion.

### On failure

Heartbeat stops.

Existing AgentRun/WorkItem failure handling remains authoritative.

### On cancellation

Heartbeat stops promptly.

Existing cancellation semantics remain authoritative.

### On timeout

Heartbeat stops.

Existing timeout semantics remain authoritative.

---

# 7. Concurrency / Race Safety

This is critical.

The heartbeat must not introduce a new race condition.

Consider:

- scheduler claiming a WorkItem
- AgentRunWorker claiming an AgentRun
- heartbeat updating the WorkItem
- recovery service scanning the WorkItem
- another Worker instance executing another AgentRun
- WorkItem being manually transitioned
- AgentRun becoming terminal
- stale recovery happening concurrently

Use the existing optimistic-concurrency mechanism.

Do not introduce:

- process-local locks
- static locks
- `lock`
- in-memory ownership flags as correctness mechanisms

A heartbeat race must never result in:

- duplicate AgentRuns
- WorkItem status corruption
- resurrecting a terminal WorkItem
- overwriting a newer `CurrentError`
- resetting a recovered WorkItem back to Running
- bypassing `WorkItemTransitions`

---

# 8. Important Recovery Race

Explicitly test this scenario:

```text
AgentRun starts
↓
WorkItem = Running
↓
heartbeat updates LastHeartbeatAtUtc
↓
WorkflowRecoveryHostedService scans
↓
WorkItem must NOT be considered stale
```

Also test:

```text
AgentRun stops heartbeating
↓
heartbeat becomes older than configured stale timeout
↓
existing recovery mechanism may recover the WorkItem
```

The test must prove that the new heartbeat actually changes recovery behavior rather than merely updating a timestamp.

---

# 9. Multi-Instance Safety

The system may eventually run multiple Worker processes.

The heartbeat implementation must therefore be safe when:

```text
Worker A → executes AgentRun A
Worker B → runs recovery
```

and:

```text
Worker A → executes AgentRun A
Worker B → runs another scheduling cycle
```

No in-memory singleton may be required for correctness.

Persisted database state is authoritative.

---

# 10. Failure of Heartbeat

Decide and document what happens if an individual heartbeat update fails.

Preferred principle:

> A transient heartbeat failure must not immediately terminate an otherwise healthy AgentRun.

The execution itself should remain authoritative.

However:

- heartbeat failures must be observable,
- they must not silently disappear,
- repeated failures must not create unbounded background work,
- the heartbeat loop must terminate when execution terminates.

Do not invent a complex retry subsystem.

Reuse existing resilience infrastructure if it already applies.

---

# 11. Background Task Lifetime

Be especially careful with asynchronous heartbeat implementation.

There must be no:

- fire-and-forget task surviving request/worker execution,
- heartbeat task continuing after AgentRun completion,
- unobserved exceptions,
- leaked `CancellationTokenSource`,
- heartbeat task surviving Worker shutdown.

Use structured async lifetime management.

The execution scope should look conceptually like:

```text
start heartbeat
try
    execute AgentRun
finally
    stop heartbeat
    await heartbeat shutdown
```

Adapt this to the existing architecture rather than copying this literally.

---

# 12. Do Not Modify Hermes

This phase is infrastructure-level.

Do NOT modify:

- `HermesAgentRuntime`
- Hermes Docker invocation
- Hermes CLI arguments
- OmniRoute
- model routing
- structured result parsing

unless repository inspection proves a change is absolutely necessary.

The heartbeat must work with any `IAgentRuntime`.

---

# 13. API / Observability

Do not add a new manual heartbeat API.

Expose useful information only through existing operational mechanisms if appropriate.

If the existing metrics infrastructure can naturally record heartbeat failures, add minimal metrics.

Do not build a new monitoring subsystem.

If no existing metric is appropriate, structured logging is sufficient.

---

# 14. Required Tests

Add focused deterministic tests.

At minimum:

### Heartbeat

1. AgentRun-backed WorkItem receives heartbeat during execution.
2. `LastHeartbeatAtUtc` advances.
3. heartbeat stops after successful execution.
4. heartbeat stops after failure.
5. heartbeat stops after cancellation.
6. heartbeat stops after timeout.

### Recovery

7. Active AgentRun WorkItem is not recovered as stale when heartbeat is fresh.
8. AgentRun WorkItem becomes recoverable when heartbeat becomes stale.
9. Existing legacy execution heartbeat behavior remains unchanged.
10. Terminal WorkItem is never resurrected by heartbeat.

### Concurrency

11. Heartbeat vs recovery race is safe.
12. Heartbeat vs terminal synchronization is safe.
13. Multiple Worker instances do not corrupt WorkItem state.
14. Optimistic concurrency conflicts are handled according to existing conventions.

### Failure handling

15. Heartbeat update failure does not incorrectly terminate healthy AgentRun execution.
16. Heartbeat failure is observable.
17. Heartbeat background work terminates correctly.

### Lifecycle

18. Worker shutdown does not leave heartbeat tasks running.
19. No unobserved heartbeat exceptions.
20. Multiple AgentRuns can heartbeat independently.

Use fake/test runtime execution with controllable delays rather than relying on real Hermes for most tests.

---

# 15. Existing Tests

Run focused existing tests first to establish the baseline.

Then run the new tests.

Do NOT rewrite unrelated tests merely to accommodate the implementation.

Do NOT weaken existing assertions.

Preserve all existing legacy scheduler/recovery tests.

---

# 16. Real Hermes Validation

A full real-Hermes stability suite is NOT required.

Phase 18.1.7/18.1.8 already verified the real Hermes execution path, and Phase 18.3 did not modify Hermes execution behavior.

However, if practical, perform one narrow real-Hermes execution long enough to cross at least one heartbeat interval and verify:

```text
AgentRun = Running
WorkItem = Running
LastHeartbeatAtUtc advances
AgentRun completes
WorkItem reaches terminal state
```

Only report this if actually executed.

Never claim real-Hermes validation without empirical evidence.

---

# 17. Persistence / Migration

First determine whether the existing schema already contains everything required.

Prefer:

> no migration

if `LastHeartbeatAtUtc` and required fields already exist.

If a migration is genuinely required, create it normally and verify:

```text
dotnet ef migrations has-pending-model-changes
```

before and after.

Do not create a migration merely to change unrelated schema.

---

# 18. Compatibility

Preserve:

- legacy `ITaskScheduler`
- `WorkItemTaskScheduler`
- `ITaskStateManager`
- `AutonomousCodingOrchestrator`
- `AgentRun`
- `AgentRunWorker`
- `AgentOrchestrator`
- `IAgentRuntime`
- `TaskDispatchScheduler`
- `AgentRunWorkItemSync`
- existing recovery behavior

The Phase 18.3 scheduler must continue to dispatch through:

```text
AgentRun → AgentRunWorker → AgentOrchestrator
```

---

# 19. Explicit Non-Goals

Do NOT implement:

- Planner Agent
- LLM planning
- task decomposition
- specification generation
- architecture generation
- coding agent
- test agent
- reviewer agent
- repair agent
- multi-agent orchestration
- OpenViking integration
- model-selection policy
- Git automation
- deployment automation
- learning/experience system
- UI/dashboard redesign
- new recovery subsystem
- manual task execution API

This phase is specifically about **AgentRun execution heartbeat and correct integration with existing WorkItem recovery**.

---

# 20. Validation Gates

Run:

```text
dotnet build apps/AI.Factory.slnx -c Debug
dotnet build apps/AI.Factory.slnx -c Release
dotnet test apps/AI.Factory.slnx -c Debug --no-build
dotnet ef migrations has-pending-model-changes
```

Run the relevant recovery/Worker/Application tests separately as useful.

For any failure:

1. determine whether it is caused by this phase,
2. distinguish deterministic failure from timing/flaky behavior,
3. rerun isolated where appropriate,
4. never hide a failure,
5. never claim success without evidence.

If the complete regression suite is not green, the phase is NOT VERIFIED.

---

# 21. Required Report

Create:

```text
.ai/context/phase-18.4-agent-heartbeat-recovery.md
```

The report must contain:

1. Executive Summary
2. Repository Discovery
3. Existing Recovery Architecture
4. Chosen Heartbeat Architecture
5. Why This Location Owns the Heartbeat
6. Configuration
7. Concurrency/Race Analysis
8. Failure Handling
9. Recovery Interaction
10. Tests Added
11. Verification Commands and Exact Results
12. Migration Status
13. Real Hermes Result, if actually run
14. Known Limitations
15. Remaining Risks
16. Final Decision

---

# 22. Final Decision

The final report MUST explicitly state:

```text
AgentRun Heartbeat & Recovery: VERIFIED
```

only if all required gates pass.

Otherwise:

```text
AgentRun Heartbeat & Recovery: NOT VERIFIED
```

and:

```text
Final Decision: NO-GO
```

Do not call the phase VERIFIED merely because the code builds.

The key proof is:

> A genuinely executing AgentRun-backed WorkItem continuously updates its persisted heartbeat sufficiently frequently that the existing stale-execution recovery does not falsely recover it, while a genuinely stale execution remains recoverable.

---

# 23. Git

Do NOT commit.

Do NOT push.

Do NOT modify remote branches.

Leave the repository changes uncommitted unless explicitly instructed otherwise.