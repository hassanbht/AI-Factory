# Phase 18.3 — Deterministic Task Scheduler & Dispatch Engine

## Summary

Connects the deterministic readiness infrastructure built in Phase 18.2
(`ITaskDependencyResolver`, `TaskReadinessService`) to controlled dispatch:
`TaskDispatchScheduler` finds `Ready` WorkItems, orders them deterministically,
claims capacity-limited candidates via the existing optimistic-concurrency
`Version` token, and creates a `Queued` `AgentRun` per claimed WorkItem for
the existing `AgentRunWorker` → `AgentOrchestrator` → `IAgentRuntime` (Hermes)
pipeline to execute. The scheduler never calls an LLM and never executes
Hermes directly.

## Discovery — two pre-existing scheduler pipelines

Repository inspection (§1) found the prompt's "TaskScheduler / TaskStateManager
legacy infrastructure" already exists and is wired into a **separate, older**
execution pipeline:

- `ITaskScheduler` / `WorkItemTaskScheduler` (concurrency hard-coded to 1,
  read-only selection) + `ITaskStateManager` / `TaskStateManager` (raw
  `Status` string mutation, bypassing `WorkItemTransitions`) + `IAgentExecutor`
  + `HermesExecution`, driven by `AutonomousCodingOrchestrator`.
- The newer AgentRun pipeline (Phase 18.1.x): `AgentRun` (with `ProjectId`/
  `WorkItemId`), `IAgentOrchestrator`/`AgentOrchestrator`, `AgentRunWorker`,
  `IAgentRuntime`/Hermes, structured `AgentExecutionInstruction`/
  `StructuredAgentResult`.

Per the phase prompt's own component list (§1: AgentRun.ProjectId/WorkItemId,
AgentRun lifecycle, AgentWorker, AgentOrchestrator, Hermes runtime, structured
contract, real Hermes+OmniRoute) and §6 ("must not bypass AgentRun/
AgentWorker/AgentOrchestrator/IAgentRuntime"), this phase's scheduler targets
the **second** pipeline. The legacy `ITaskScheduler`/`WorkItemTaskScheduler`/
`ITaskStateManager`/`AutonomousCodingOrchestrator` are **untouched** — no
existing file in that pipeline was modified, and its own tests
(`WorkItemTaskSchedulerTests`, `AutonomousCodingOrchestratorTests`) still
pass unchanged. A new, separate `ITaskDispatchScheduler` was introduced
instead of extending `ITaskScheduler`, because extending it would have meant
either breaking its single-item/concurrency-1 contract (used by
`AutonomousCodingOrchestrator`) or silently changing legacy-pipeline
behavior — both against §1's "search all usages before changing any existing
scheduler/state-manager API."

## Architecture

```
WorkItem (Status == Ready, TaskReadinessStatus == Ready)
   |
ITaskDependencyResolver.Resolve()   <- reused unmodified, no second DFS
   |
TaskDispatchScheduler.RunCycleAsync(projectId)
   |  deterministic order -> concurrency-limited claim -> AgentRun
   v
AgentRun (Queued)  --[AgentRunWorker.TryClaimNextPendingAsync]-->
AgentOrchestrator --> IAgentRuntime (Hermes) --> terminal AgentRun status
   |
AgentRunWorkItemSync.SyncAsync()   <- generic completion mapping
   v
WorkItem (Completed / Failed / Cancelled)
```

New/changed components:

- `ITaskDispatchScheduler` / `TaskDispatchScheduler`
  (`Application/Abstractions/ITaskDispatchScheduler.cs`,
  `Application/Orchestration/TaskDispatchScheduler.cs`)
- `IAgentRunWorkItemSync` / `AgentRunWorkItemSync`
  (`Application/Abstractions/IAgentRunWorkItemSync.cs`,
  `Application/Orchestration/AgentRunWorkItemSync.cs`) — wired into
  `AgentRunWorker.PollOnceAsync` after `IAgentOrchestrator.ExecuteAsync`.
- `TaskSchedulerOptions` (`Application/Configuration/TaskSchedulerOptions.cs`)
  — `Enabled`, `MaxConcurrentTasks` (default 1), `SchedulingInterval`
  (default 15s), bound from config section `"TaskScheduler"`.
- `ISchedulerStatusTracker` / `SchedulerStatusTracker` — minimal in-memory
  status snapshot (last cycle, cumulative dispatched/failures).
- `TaskSchedulerHostedService` (Worker) — periodic driver: one sweep across
  every non-terminal `Project` per tick, calling `RunCycleAsync` per project.
- `GET /api/scheduler/status` — read-only status endpoint.
- 6 new `IOperationalMetrics` counters: `RecordSchedulingCycleRun`,
  `RecordSchedulerTaskDispatched`, `RecordSchedulerDispatchFailure`,
  `RecordSchedulerRaceLost`, `RecordSchedulerConcurrencyLimitSkipped(count)`.

## Deterministic ordering (§4)

Within one project's `RunCycleAsync` call, candidates are WorkItems where
both:

1. `TaskReadinessStatus` (from `ITaskDependencyResolver.Resolve`) == `Ready`
2. `WorkItem.Status` == `WorkItemStatus.Ready` (see "Readiness vs. lifecycle
   status" below)

...ordered by: Priority rank (`Critical` < `High` < `Normal` < `Low`, same
table as `WorkItemTaskScheduler`) → `CreatedAtUtc` ascending → `Id` (`Guid`)
ascending as the final stable tie-breaker. This is a pure, deterministic sort
over already-loaded, unmodified data — the same DB state always produces the
same ordering.

### Readiness vs. lifecycle status (a real design finding)

`ITaskDependencyResolver.Classify` (unmodified) treats *any* non-terminal,
non-`Running` own status — including `Draft` — as `TaskReadinessStatus.Ready`
once dependencies are satisfied; it only measures dependency satisfaction,
not lifecycle stage. But `WorkItemTransitions` only allows `Ready -> Running`
(not `Draft -> Running`), and §9 requires the scheduler use the guarded
`WorkItem.TransitionTo` API, never raw `Status` assignment (unlike the legacy
`TaskStateManager.TryStartAsync`, which does assign the raw string and can
therefore claim a `Draft` item). The scheduler therefore additionally
requires `WorkItem.Status == WorkItemStatus.Ready` before it will claim an
item — a `Draft` WorkItem must first be moved to `Ready` via the existing
`PATCH /api/tasks/{id}/status` (Phase 18.2) before it becomes
scheduler-eligible. This is a deliberate, minimal difference from the legacy
scheduler's looser behavior, required to keep the transition guarded.

## Concurrency & atomic claim (§7, §9)

Concurrency is scoped **per project** (same scope as the legacy
`WorkItemTaskScheduler`'s hard-coded concurrency-1): `capacity =
max(0, MaxConcurrentTasks - <count of Running WorkItems in the project>)`.
Ready candidates beyond `capacity` are skipped this cycle (not lost — they
remain `Ready` and are picked up next cycle) and counted in
`SkippedDueToConcurrencyLimit`.

The atomic claim is `WorkItem.TransitionTo(Running)` +
`IWorkItemRepository.UpdateAsync`. No new locking mechanism was introduced —
`WorkItem.Version` is already an EF `IsConcurrencyToken()` column (added by
the pre-existing `AddOptimisticConcurrencyTokens` migration), and
`FactoryDbContext.SaveChangesAsync` already converts a losing
`DbUpdateConcurrencyException` into `ConcurrencyConflictException` for any
`IVersionedEntity`. A concurrent scheduler cycle (same process or a different
one — enforced at the SQLite row level, not in-memory) that already claimed
the same WorkItem causes the loser's `UpdateAsync` to throw
`ConcurrencyConflictException`, which is caught and reported as
`TaskDispatchSkipReason.ConcurrentClaimLost` — never a duplicate dispatch.
Verified directly by `TaskDispatchSchedulerTests.
RunCycleAsync_ConcurrentCycles_NoDuplicateDispatch` (two independent
`DbContext`s racing `RunCycleAsync` concurrently — no in-memory lock, relies
purely on the DB-level optimistic-concurrency token).

An `AgentDefinition` match (§8, looked up by convention:
`AgentDefinition.AgentType == WorkItem.Type.ToString()`, enabled only) is
resolved **before** the claim — if none exists, the WorkItem is skipped
(`TaskDispatchSkipReason.NoAgentDefinition`) without ever being claimed, so a
task with no runnable agent is never left stranded in `Running`.

## Dispatch model (§6, §8)

On a successful claim, the scheduler creates an `AgentRun` (`ProjectId`,
`WorkItemId`, `AgentDefinitionId`, `Input` = WorkItem title+description) via
the existing `IAgentRunRepository.CreateAsync` (idempotent on
`CorrelationId`, unchanged) and applies the same `Created -> Queued`
transition `AgentRunService.CreateAsync` already uses. The scheduler stops
there — it never calls `IAgentOrchestrator`/`IAgentRuntime` itself.
`AgentRunWorker.TryClaimNextPendingAsync` picks up the `Queued` row on its
own polling cadence, exactly as it does for any other `AgentRun`.

## WorkItem completion mapping (§10)

`AgentRunWorkItemSync.SyncAsync` is called from `AgentRunWorker.PollOnceAsync`
immediately after `IAgentOrchestrator.ExecuteAsync` returns `Executed: true`
for a run with a `WorkItemId`. It is intentionally generic (no
Planner/Coder/Tester/Reviewer-specific behavior):

| AgentRun terminal status | WorkItem target |
|---|---|
| `Succeeded` | `Completed` |
| `Failed` | `Failed` |
| `Cancelled` | `Cancelled` |
| `TimedOut` | `Failed` (see below) |

`WorkItemStatus` has no dedicated `TimedOut` value — documented policy:
`TimedOut` maps to `Failed`, with `WorkItem.CurrentError` recording that the
underlying cause was specifically a timeout, not a generic failure.

The sync only acts when the WorkItem is currently `Running` — an
already-terminal WorkItem (moved independently via the API, or already
synced) is left untouched, verified by
`AgentRunWorkItemSyncTests.SyncAsync_AlreadyTerminalWorkItem_NotOverwritten`.
A lost `ConcurrencyConflictException` race here is treated the same way as
the scheduler's claim race — logged and skipped, never retried/forced.

## Recovery (§14)

No new recovery system was introduced. `WorkflowRecoveryHostedService` +
`ITaskStateManager.RecoverOrphanedTasksAsync`/`RecoverStaleExecutionsAsync`
already sweep **every** WorkItem in `Running` status unconditionally — it has
no notion of which pipeline put a WorkItem into `Running`, so a WorkItem
claimed by `TaskDispatchScheduler` is already covered by the existing
startup/stale sweep.

**Known limitation**: that sweep's staleness check uses
`LastHeartbeatAtUtc ?? StartedAtUtc`. Only the legacy
`AutonomousCodingOrchestrator` path calls `ITaskStateManager.HeartbeatAsync`
during execution; the new AgentRun pipeline does not, so a WorkItem dispatched
by `TaskDispatchScheduler` and genuinely still executing past
`RecoveryOptions.StaleExecutionTimeout` would be incorrectly recovered
(reset to `Repairing`/`Failed`) by the existing stale sweep. This is an
interaction between an existing component and this phase's new dispatch
path, not a new recovery bug — flagged as next-phase work (wire
`AgentOrchestrator`/`AgentRunWorker` into `ITaskStateManager.HeartbeatAsync`,
or give the AgentRun pipeline its own staleness signal) rather than solved
here, per §14 ("do not introduce an independent recovery system" and "do not
guess — document the selected policy").

## Metrics (§16)

Added to `IOperationalMetrics` (single implementation, `OperationalMetrics`):
`RecordSchedulingCycleRun`, `RecordSchedulerTaskDispatched`,
`RecordSchedulerDispatchFailure`, `RecordSchedulerRaceLost`,
`RecordSchedulerConcurrencyLimitSkipped(count)` — exposed on
`OperationalMetricsSnapshot` and readable via the existing
`GET /api/operations/metrics`.

## API (§15)

`GET /api/scheduler/status` (rate-limited `GeneralApi`, same as every other
endpoint) returns `SchedulerStatusSnapshot`: `enabled`, `maxConcurrentTasks`,
`lastCycleAtUtc`, `lastCycleProjectId`, `lastCycleDispatchedCount`,
`lastCycleFailureCount`, `lastCycleSkippedDueToConcurrency`,
`totalDispatched`, `totalFailures`. No manual "execute arbitrary task"
endpoint was added (explicitly out of scope, §15).

**Known limitation**: `ISchedulerStatusTracker` is an in-memory singleton, and
`TaskSchedulerHostedService` (which actually runs cycles) lives in the
`AI.Factory.Worker` process while the HTTP endpoint is hosted by
`AI.Factory.Api` — two separate processes. In the current split-process
deployment, `/api/scheduler/status` reflects only the querying process's own
in-memory state, so it will show zero activity unless something in the Api
process itself calls `RunCycleAsync`. This is the same pre-existing
characteristic as `/api/operations/metrics`/`IOperationalMetrics` (registered
as a singleton in both processes independently) — not a new limitation
introduced by this phase, and out of scope to fix here (would need either a
shared process or a persisted/IPC status channel, neither justified by this
phase's requirements).

## Persistence / migrations (§19)

None. `WorkItem.Version` and `AgentRun.Version` were already
`IsConcurrencyToken()` columns from the pre-existing
`AddOptimisticConcurrencyTokens` migration; no schema change was needed.
Confirmed via `dotnet ef migrations has-pending-model-changes` →
"No changes have been made to the model since the last migration."

## Tests (§17)

New files:

- `AI.Factory.Application.Tests/TaskDispatchSchedulerTests.cs` (10 tests) —
  selection (single ready, priority ordering, stable `Id` tie-breaker),
  dependency behavior (pending dependency blocks dispatch), dispatch
  (`Queued` `AgentRun` linked to Project+WorkItem, WorkItem → `Running`),
  idempotency (repeated cycle → one active `AgentRun`), concurrency (limit
  enforced, remainder stays `Ready`), a genuine **race condition test**
  (two independent `DbContext`s racing `RunCycleAsync` concurrently — no
  in-memory lock, relies purely on the DB-level optimistic-concurrency
  token), and failure isolation (a WorkItem with no matching
  `AgentDefinition` is skipped, untouched, and does not block a sibling
  WorkItem's dispatch in the same cycle).
- `AI.Factory.Application.Tests/AgentRunWorkItemSyncTests.cs` (6 tests) —
  all four terminal-status mappings, already-terminal WorkItem not
  overwritten, no-`WorkItemId` no-op.
- `AI.Factory.Api.Tests/SchedulerStatusApiTests.cs` (1 test) — status
  endpoint shape.

## Compatibility

No existing file's public behavior changed except:
`AgentRunWorker.PollOnceAsync` gained one additional step (WorkItem sync)
that only executes when `run.WorkItemId` is set — a run without a
`WorkItemId` (the existing/only way `AgentRun`s were created before this
phase) is completely unaffected. `IOperationalMetrics` gained 5 new members;
its only implementation (`OperationalMetrics`) was updated, no other
implementers exist. The legacy `ITaskScheduler`/`ITaskStateManager`/
`AutonomousCodingOrchestrator` pipeline is untouched.

## Known limitations (recap)

1. Recovery staleness for AgentRun-pipeline WorkItems relies on `StartedAtUtc`
   only (no heartbeat wired in yet) — see "Recovery" above.
2. `/api/scheduler/status` only reflects the querying process's own
   in-memory state (split Api/Worker processes) — see "API" above.
3. `AgentDefinition` lookup is by naming convention
   (`AgentType == WorkItem.Type.ToString()`) — no seed/catalog exists yet;
   an operator must register a matching, enabled `AgentDefinition` per
   `WorkItemType` they want dispatched. This is deliberate (§20: no new
   agent types/behavior invented here).

## Real Hermes (§18)

Not run this session. This phase does not modify `AgentOrchestrator`,
`IAgentRuntime`, or any Hermes execution behavior — it only adds a new
*caller* of the existing, already-verified `AgentRunWorker` →
`AgentOrchestrator` → Hermes path (Phase 18.1.7/18.1.8 already cover that
path's real-Hermes E2E behavior). Per the phase's own guidance ("do not
repeat the full stability suite unless AgentRun/Hermes execution behavior is
materially modified") and the standing instruction never to claim untested
evidence, no real-Hermes run was performed or is claimed here.

## Next-phase recommendation

Wire `AgentOrchestrator`/`AgentRunWorker` execution into
`ITaskStateManager.HeartbeatAsync` (or an equivalent AgentRun-pipeline
staleness signal) so the existing stale-execution sweep does not
false-positive on a genuinely still-running AgentRun-dispatched WorkItem —
this is the one open item flagged as a known limitation above, not solved in
this phase per §14's "do not guess" instruction.

## Verification

- `dotnet build apps/AI.Factory.slnx -c Debug` → 0 Warnung(en), 0 Fehler
- `dotnet build apps/AI.Factory.slnx -c Release` → 0 Warnung(en), 0 Fehler
- `dotnet test apps/AI.Factory.slnx -c Debug --no-build` →
  **963/963 passed** (74 Worker.Tests + 619 Application.Tests +
  270 Api.Tests; one `DatabaseResiliencePolicyTests` timing test flaked once
  during an interim run and passed both in isolation and on the final full
  re-run — pre-existing, timing-sensitive, untouched by this phase).
- `dotnet ef migrations has-pending-model-changes` (from
  `apps/src/AI.Factory.Api`) → "No changes have been made to the model since
  the last migration."

**Task Scheduler: VERIFIED**

**Final Decision: GO**
