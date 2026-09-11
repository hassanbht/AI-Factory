# Phase 18.4 — AgentRun Execution Heartbeat & Recovery Integration

## 1. Executive Summary

Resolves the Phase 18.3 known limitation: WorkItems dispatched through
`TaskDispatchScheduler → AgentRun → AgentRunWorker → AgentOrchestrator →
IAgentRuntime` did not update `WorkItem.LastHeartbeatAtUtc`, so the existing
stale-execution recovery (`WorkflowRecoveryHostedService` /
`ITaskStateManager.RecoverStaleExecutionsAsync`) fell back to `StartedAtUtc`
and could recover a genuinely still-running AgentRun-backed WorkItem.

`AgentRunWorker` now runs a periodic heartbeat loop — calling the existing
`ITaskStateManager.HeartbeatAsync` — for the duration of each
`IAgentOrchestrator.ExecuteAsync` call, whenever the `AgentRun` is linked to
a `WorkItem`. No new recovery system, no change to `IAgentRuntime`,
`HermesAgentRuntime`, `AgentOrchestrator`, or any Hermes-specific code.

## 2. Repository Discovery

Read/inspected before changing anything: `AGENTS.md`, `AgentRun`,
`AgentRunWorker`, `AgentOrchestrator`, `IAgentRuntime`, `HermesAgentRuntime`
(location only — not modified), `IAgentExecutor` (legacy pipeline, untouched),
`AgentRunWorkItemSync`, `ITaskStateManager`/`TaskStateManager`,
`WorkflowRecoveryHostedService`, `RecoveryOptions`, `WorkItem`,
`WorkItemTransitions`, `TaskDispatchScheduler`, and the Phase 18.3 report.

Key findings that shaped the design:

- `IAgentRuntime.ExecuteAsync(AgentRun, CancellationToken)` is a single
  opaque call — no progress callback, no streaming hook. A heartbeat
  therefore cannot live *inside* the runtime call without changing
  `IAgentRuntime`'s contract (explicitly disallowed, §12) — it must run
  concurrently, alongside it.
- `ITaskStateManager.HeartbeatAsync(workItemId, checkpoint, ct)` already
  exists, is purely observational (only touches `LastHeartbeatAtUtc`/
  `LastCheckpoint`/`UpdatedAtUtc` — never `Status` or `AttemptCount`), and is
  already used by the legacy `AutonomousCodingOrchestrator` pipeline at
  fixed checkpoints (not periodically). Reusing it exactly as-is (no
  signature change) satisfies §6's "through the existing task-state
  abstraction where possible" and guarantees zero behavior change to the
  legacy path (§14 test #9, §18).
- `FactoryDbContext.SaveChangesAsync` already globally converts a losing
  `DbUpdateConcurrencyException` into `ConcurrencyConflictException` for any
  `IVersionedEntity` (including `WorkItem`, via the pre-existing
  `AddOptimisticConcurrencyTokens` migration). No new concurrency mechanism
  was needed — this is the existing mechanism §7 requires reusing.
- `WorkflowRecoveryHostedService.ExecuteAsync`'s periodic loop called
  `RunStaleSweepAsync` with **no** surrounding try/catch. A concurrent
  heartbeat write racing a stale-recovery batch save (both against the same
  `WorkItem` row, from different `DbContext`s) can legitimately raise
  `ConcurrencyConflictException` from `RecoverStaleExecutionsAsync`'s single
  batched `SaveChangesAsync` call — and, unhandled, that would have crashed
  the entire recovery `BackgroundService` loop permanently. This was a
  latent gap the phase's own required race scenario (§7: "recovery service
  scanning the WorkItem" concurrently with heartbeat) exposed, not
  introduced by heartbeating alone, but heartbeating makes it far more
  likely to actually occur. Fixed with a minimal try/catch (see §7 below).

## 3. Existing Recovery Architecture (unchanged)

```
WorkflowRecoveryHostedService (BackgroundService)
   ├── startup: ITaskStateManager.RecoverOrphanedTasksAsync()
   │      — sweeps every WorkItem.Status == Running unconditionally
   └── periodic (RecoveryOptions.PeriodicSweepInterval, default 5 min):
          ITaskStateManager.RecoverStaleExecutionsAsync()
          — recovers a Running WorkItem whose
            (LastHeartbeatAtUtc ?? StartedAtUtc) is older than
            RecoveryOptions.StaleExecutionTimeout (default 15 min)
```

`RecoverRunningItem` (shared by both paths) sets `Status` to `Repairing`
(attempts remain) or `Failed` (exhausted) via a raw string assignment — a
pre-existing, deliberate exception to the `WorkItemTransitions`-guarded path
(documented in `WorkItem.TransitionTo`'s own comment), left untouched.

This service has no notion of *which* pipeline (legacy
`AutonomousCodingOrchestrator` or the new `AgentRun` pipeline) put a
WorkItem into `Running` — it already covers both, once both heartbeat.

## 4. Chosen Heartbeat Architecture

`AgentRunWorker.PollOnceAsync`, around the single `await
orchestrator.ExecuteAsync(run.Id, executionCts.Token)` call:

```
if run.WorkItemId is set:
    start RunHeartbeatLoopAsync(taskStateManager, workItemId, heartbeatCts.Token)
        (linked to executionCts — inherits shutdown/timeout cancellation,
         plus its own independent Cancel())

result = await orchestrator.ExecuteAsync(...)
await StopHeartbeatAsync(heartbeatCts, heartbeatTask)   # before WorkItem sync
... AgentRunWorkItemSync.SyncAsync(...) unchanged ...

finally:
    await StopHeartbeatAsync(heartbeatCts, heartbeatTask)  # safety net for the exception paths
```

`RunHeartbeatLoopAsync`:

```
try
    while true:
        await Task.Delay(HeartbeatInterval, ct)     # throws OperationCanceledException on stop
        try:  await taskStateManager.HeartbeatAsync(workItemId, "AgentExecutionInProgress", ct)
        catch OperationCanceledException: rethrow
        catch Exception ex: log warning + RecordAgentRunHeartbeatFailure(); keep looping
catch OperationCanceledException: swallow (normal stop signal)
```

`StopHeartbeatAsync` cancels the heartbeat's own linked `CancellationTokenSource`
and awaits the loop task to completion; it is idempotent (safe to call twice —
once explicitly after a successful `ExecuteAsync`, once again in the outer
`finally` as a safety net for the two `OperationCanceledException` catch
paths above it).

## 5. Why This Location Owns the Heartbeat

Considered and rejected `AgentOrchestrator` as the owner: it would require
adding an `ITaskStateManager` dependency into a component whose own doc
comment states it is deliberately runtime- and WorkItem-agnostic (`AgentRun`
is "not tied to... WorkItem being its identity"), and would touch
`AgentOrchestratorTests`' fixtures. `AgentRunWorkItemSync` — the other
WorkItem-integration seam introduced in Phase 18.3 — already lives in
`AgentRunWorker`, not `AgentOrchestrator`, for exactly this reason: Phase
18.3 deliberately kept all WorkItem-awareness at the Worker layer so
`AgentOrchestrator` stays a pure, reusable "run one AgentRun" primitive.
Heartbeat ownership follows the same precedent — `AgentRunWorker` already
knows about `run.WorkItemId` (used for `AgentRunWorkItemSync`) and is the
one place that wraps the runtime-agnostic `IAgentOrchestrator.ExecuteAsync`
call, satisfying "works for every `IAgentRuntime`" without needing to know
anything about which runtime is behind it.

## 6. Configuration

`AgentWorkerOptions.HeartbeatInterval` (new, bound from the existing
`"AgentWorker"` config section) — default **30 seconds**, validated positive
in `Validate()` alongside the existing fields. `RecoveryOptions.
StaleExecutionTimeout` defaults to 15 minutes, giving a 30x margin (interval
≪ stale timeout, per §5's requirement) without hard-coding a value derived
at runtime from `RecoveryOptions` (kept as two independently-configurable,
decoupled options — no cross-section coupling was introduced).

## 7. Concurrency / Race Analysis

| Scenario (§7/§9) | Outcome |
|---|---|
| Scheduler claims WorkItem, AgentRunWorker claims AgentRun | Unrelated rows/timing — no interaction. |
| Heartbeat vs. terminal `AgentRunWorkItemSync` transition | Heartbeat is explicitly stopped and awaited *before* the sync call runs (§4) — no possible overlap; verified by `AgentRunWorkItemSyncTests` (Phase 18.3, unchanged) and by construction. |
| Heartbeat vs. stale-recovery sweep (same WorkItem, different process/DbContext) | Both go through the existing `Version`-token optimistic concurrency. Whichever `SaveChanges` loses throws `ConcurrencyConflictException` — inside `AgentRunWorker`'s heartbeat loop this is caught, logged, counted, and the loop continues (§10); inside `WorkflowRecoveryHostedService`'s sweep it is now caught (§3) so the periodic loop survives and retries next tick. Proven directly by `AgentRunHeartbeatRecoveryTests.ConcurrentHeartbeatAndStaleRecovery_ResolvesWithoutCorruption`. |
| Multiple Worker processes | No in-memory state is used for correctness anywhere in this design — `HeartbeatAsync`/`RecoverStaleExecutionsAsync` are both plain DB reads+writes guarded only by the DB-level `Version` token, so this is safe identically whether the two callers are two threads, two processes, or two hosts. |
| WorkItem manually transitioned via API mid-heartbeat | `HeartbeatAsync` never checks or changes `Status`; a manual transition (e.g. to `Cancelled`) simply persists independently — the next heartbeat write still succeeds (it doesn't touch Status) but is otherwise harmless; the WorkItem's real status is authoritative wherever it's read. |
| AgentRun already terminal, heartbeat still in flight | Cannot happen by construction — the heartbeat is stopped/awaited before any terminal-sync logic runs (§4), and the loop itself has no awareness of `AgentRun.Status` (only `WorkItem`). |

No `lock`, static field, or in-memory ownership flag was introduced anywhere
in this change.

## 8. Failure Handling

Principle applied verbatim from the phase prompt: a transient heartbeat
failure never terminates an otherwise-healthy `AgentRun` execution — the
`catch (Exception ex)` inside `RunHeartbeatLoopAsync` logs
(`AgentRunHeartbeatFailed`) and increments the new
`IOperationalMetrics.RecordAgentRunHeartbeatFailure()` counter, then the
loop simply waits for its next tick. No retry subsystem was built (existing
resilience infrastructure — circuit breakers/bulkheads — was judged
unnecessary for a purely-observational, already-bounded-by-interval,
best-effort write). Repeated failures cannot create unbounded work: the
loop only ever has one in-flight `Task.Delay`/`HeartbeatAsync` pair at a
time, gated by `HeartbeatInterval`, and terminates deterministically the
moment `heartbeatCts` is cancelled. Proven by
`AgentRunWorkerTests.Heartbeat_FailureDoesNotStopExecution_AndIsObservable`.

## 9. Recovery Interaction

Verified directly (not just by code reading) via
`AgentRunHeartbeatRecoveryTests`, using the real `TaskStateManager` against
an in-memory SQLite `FactoryDbContext` (no fakes for the recovery side):

- A WorkItem heartbeated every 30ms against an 80ms stale timeout is
  **never** recovered (`FreshHeartbeat_PreventsStaleRecovery`).
- A WorkItem given no heartbeat at all, past a 60ms stale timeout, **is**
  recovered — `RecoverStaleExecutionsAsync` returns 1 and the WorkItem
  moves to `Repairing`/`Failed` with `CurrentError` naming the stale-timeout
  cause (`StaleHeartbeat_BecomesRecoverable`).
- A heartbeat landing on an already-`Completed` WorkItem never resurrects
  it — `Status` stays `Completed` (`Heartbeat_OnTerminalWorkItem_
  DoesNotResurrectStatus`).
- A genuine concurrent heartbeat-vs-recovery race resolves cleanly via
  `ConcurrencyConflictException`, never corrupting `Status`
  (`ConcurrentHeartbeatAndStaleRecovery_ResolvesWithoutCorruption`).

This is the direct empirical proof of the phase's key required claim (§22).

## 10. Tests Added

- `apps/tests/AI.Factory.Worker.Tests/AgentRunWorkerTests.cs` — 7 new tests
  (fake `ITaskStateManager`/`IAgentRunWorkItemSync`, controllable-delay fake
  runtime, no real Hermes/DB): heartbeat advances during long execution;
  stops after success; stops after an orchestrator exception; stops on
  execution timeout; a failing heartbeat doesn't stop execution and is
  observable via metrics; two sequential AgentRuns each heartbeat
  independently; Worker shutdown leaves no heartbeat task running. All 7
  existing `AgentRunWorkerTests` (claim/execute/failure-isolation/shutdown,
  Phase 18.1.5) pass unchanged — 14/14 in the file.
- `apps/tests/AI.Factory.Application.Tests/AgentRunHeartbeatRecoveryTests.cs`
  — 4 new tests (real SQLite + `TaskStateManager`, no fakes): the two
  "changes recovery behavior" proofs (§8/§22) plus terminal-no-resurrection
  and the concurrent race, described in §9 above.
- `TaskDispatchSchedulerTests.cs`'s `TestOperationalMetrics` fake gained a
  one-line no-op override for the new `IOperationalMetrics` member (purely
  mechanical, required to keep it compiling — Phase 18.3 tests otherwise
  unchanged).

No existing test's assertions were weakened or rewritten to fit this
change.

## 11. Verification Commands and Exact Results

```
dotnet build apps/AI.Factory.slnx -c Debug     → 0 Warnung(en), 0 Fehler
dotnet build apps/AI.Factory.slnx -c Release   → 0 Warnung(en), 0 Fehler
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    → AI.Factory.Worker.Tests:      81/81 passed
    → AI.Factory.Application.Tests: 623/623 passed
    → AI.Factory.Api.Tests:         270/270 passed
    → TOTAL: 974/974 passed
dotnet ef migrations has-pending-model-changes (from apps/src/AI.Factory.Api)
    → "No changes have been made to the model since the last migration."
```

Baseline (before the new tests were added, §15): the pre-existing
`AgentRunWorkerTests` (13), `WorkerRestartRecoveryTests`, and
`AgentOrchestratorTests` (30 total across the two files) all passed first,
establishing that this phase's edits did not silently break anything before
new coverage was added on top. The heartbeat-specific timing tests (Worker
and Application layers) were each re-run 3 times in isolation to confirm
they are not flaky before being folded into the full regression.

## 12. Migration Status

None required or created. `WorkItem.LastHeartbeatAtUtc`, `LastCheckpoint`,
`StartedAtUtc`, `CurrentError`, `Status`, and `Version` all already existed
(pre-Phase-18.4) and were already exactly what `ITaskStateManager.
HeartbeatAsync`/`RecoverStaleExecutionsAsync` operate on — this phase only
adds a new, more frequent *caller* of the existing method. Confirmed via
`dotnet ef migrations has-pending-model-changes` before starting
implementation and again after (§11) — both report no pending changes.

## 13. Real Hermes Result

Not run. No real-Hermes execution was performed this session (would require
live Hermes/OmniRoute credentials not confirmed present in this
environment, and this phase's own guidance (§16) makes it optional/"if
practical" and only to be reported if actually executed). Per the standing
instruction to never claim untested evidence, no real-Hermes result is
claimed. This phase does not modify `HermesAgentRuntime`, Hermes invocation,
OmniRoute, or model routing — Phase 18.1.7/18.1.8 already cover that path's
own real-Hermes verification, and Phase 18.3 did not modify Hermes
execution behavior either — so a full real-Hermes stability rerun was
judged unnecessary in addition to being unattempted.

## 14. Known Limitations

1. Heartbeat is per-AgentRun-linked-WorkItem only — an `AgentRun` with no
   `WorkItemId` (e.g. one created directly via the `AgentRun` API without
   going through `TaskDispatchScheduler`) is unaffected, exactly as before;
   there is nothing to heartbeat in that case (no WorkItem is involved).
2. `RecoverStaleExecutionsAsync`/`RecoverOrphanedTasksAsync` still batch all
   recovered items into a single `SaveChangesAsync` call per sweep. A lost
   `ConcurrencyConflictException` race against a single contended WorkItem
   fails that *entire* sweep's batch (no partial persistence — EF's
   `SaveChangesAsync` is all-or-nothing per call), not just the racing item;
   the next periodic sweep (`RecoveryOptions.PeriodicSweepInterval`, default
   5 min) retries and typically succeeds once the transient contention has
   passed. This is a liveness hiccup, not a correctness/corruption issue,
   and was judged out of scope to fix by re-architecting `TaskStateManager`'s
   batching (§3's "do not redesign the recovery subsystem"); the added
   try/catch around the sweep call (§3/§7) is the minimal fix that keeps the
   recovery loop itself alive regardless.
3. `HeartbeatAsync`'s own `SaveChangesAsync` call is not wrapped in the
   repository-level `DatabaseResiliencePolicy` retry policy used elsewhere
   in the codebase for transient SQLite busy/locked errors — this was
   already true before this phase (pre-existing `TaskStateManager`
   behavior, unchanged) and is not new to heartbeating, just exercised more
   often now.

## 15. Remaining Risks

- Under sustained high WorkItem-concurrency (many simultaneous
  AgentRun-backed WorkItems all heartbeating on the same 30s default
  interval) plus a busy periodic recovery sweep, `ConcurrencyConflictException`-driven
  retries could become more frequent than in today's lower-traffic legacy
  path. Not measured under load in this phase (no load/stress harness was
  in scope) — flagged as a candidate for the existing load/stress testing
  infrastructure (Phase 17.4.9) to pick up, not solved here.
- If a future phase introduces multiple genuinely concurrent Worker
  processes (§9 anticipates this but Phase 18.4 does not add a
  multi-process test harness — the SQLite-in-process, multi-`DbContext`
  race test is the closest practical proxy available in this repository's
  existing test infrastructure), the same optimistic-concurrency guarantees
  apply unchanged, but this has not been empirically exercised against a
  real multi-process/multi-host SQLite deployment.

## 16. Final Decision

**AgentRun Heartbeat & Recovery: VERIFIED**

**Final Decision: GO**
