# Phase 18.13 — WorkItem Execution Pipeline Unification

## 1. Dependency Analysis (empirical, re-verified before deletion)

Repo-wide `grep -rn` for each legacy type, before any deletion, confirmed:

- `IAutonomousCodingOrchestrator.ExecuteNextReadyTaskAsync` — **zero** production callers anywhere in `apps/src`.
- `AutonomousCodingOrchestrator.ExecuteSpecificWorkItemAsync` — called **only** from `OrchestrationRunExecutionEngine.RunAttemptLoopAsync` (now deleted).
- `ITaskScheduler` / `WorkItemTaskScheduler` — only production consumer was `AutonomousCodingOrchestrator`'s constructor injection.
- `IHermesExecutionRepository` / `HermesExecutionRepository` — only production consumer was `AutonomousCodingOrchestrator`.
- `IValidationEngine` / `ValidationEngine` — only production consumer was `AutonomousCodingOrchestrator`.
- `ICodeReviewer`, `IReviewContextBuilder`/`ReviewContextBuilder`, `IEvaluationEngine`/`EvaluationEngine`, `LearningService`, `IValidationRunRepository`, `IReviewRunRepository`, `IEvaluationRunRepository` — generic, Application-layer services with no legacy coupling (`ReviewContextBuilder` depends only on `IContextProvider`/`IArtifactRepository`/`IValidationRunRepository`) — confirmed reusable, relocated rather than deleted.

## 2. Responsibility Matrix (before → after)

| Capability | Before (legacy path) | After (unified) |
|---|---|---|
| Task selection | `ITaskScheduler.GetNextReadyWorkItemAsync` (legacy) | `TaskDispatchScheduler.RunCycleAsync` (unchanged) |
| Manual/API-triggered selection | `AutonomousCodingOrchestrator.ExecuteSpecificWorkItemAsync` | `TaskDispatchScheduler.DispatchWorkItemAsync` (new) |
| AgentRun creation | N/A (legacy created `HermesExecution`, not `AgentRun`) | `TaskDispatchScheduler` (unchanged) |
| Agent invocation | `IAgentExecutor` direct call inside the orchestrator | `AgentRunWorker` → `AgentOrchestrator` → `IAgentRuntime` (unchanged) |
| Build+test | `IValidationEngine.ValidateAsync` (legacy, own `ValidationRun`) | `IVerificationGate.VerifyAsync` (unchanged); `WorkItemCompletionProcessor` now also persists a `ValidationRun` row purely so `IReviewContextBuilder` has data to read |
| Review | Inline in `AutonomousCodingOrchestrator.ExecuteWorkItemCoreAsync` | `WorkItemCompletionProcessor.ProcessAsync` (relocated, same policy) |
| Evaluation | `AutonomousCodingOrchestrator.EvaluateAndLearnAsync` | `WorkItemCompletionProcessor.ProcessAsync` (relocated) |
| Learning | Same method | Same, relocated; `solution` now sourced from `AgentRun.Output` instead of `IHermesExecutionRepository.GetLatestAsync(...)?.Response` |
| Repair attempt counting | `WorkItem.AttemptCount` (legacy, via `ITaskStateManager.TryStartAsync`) | `WorkItem.RepairAttemptCount` (via `TaskDispatchScheduler.DispatchRepairAsync`) — `EvaluationEngine.RepairCount` now reads the correct field |
| Heartbeat | `ITaskStateManager.HeartbeatAsync` (legacy, dead once `AutonomousCodingOrchestrator` is gone) | `AgentRunWorker`'s own heartbeat (unchanged; `ITaskStateManager` itself is retained — still used by `WorkflowRecoveryHostedService`/`CriticalServicesHealthCheck`) |
| Cancellation | `OrchestrationRun.CancellationRequestedAtUtc` flag, never actually enforced against a running Hermes call | `OrchestrationService.RequestCancellationAsync` → `WorkItem.TransitionTo(Cancelled)`, cooperative, same as legacy in effect (never preemptive) |
| Completion authority | `AutonomousCodingOrchestrator` decided Completed directly | `AgentRunWorkItemSync` + `IVerificationGate` + `IWorkItemCompletionProcessor` (one authority) |
| Trace/step recording | `OrchestrationRunExecutionEngine`'s own `StartStepAsync`/`CompleteStepAsync`/`FailStepAsync` | Removed — manual runs no longer produce per-stage `OrchestrationRunStep` rows (accepted tradeoff, documented below) |

## 3. Target Architecture

Implemented exactly as specified:

```
Scheduler / Manual API → AgentRun → AgentRunWorker → AgentOrchestrator
    → IAgentRuntime → Hermes → StructuredAgentResult → WorkItem State
    → Verification → (Repair | WorkItemCompletionProcessor → Completed)
```

`OrchestrationRunExecutionEngine` now only: loads Project/WorkItem, calls
`ITaskDispatchScheduler.DispatchWorkItemAsync`, records `AgentRunId`, and
leaves the run in `Executing`. It never calls build/test/review/evaluation
logic itself.

## 4. Classification of `OrchestrationRun` / `/api/orchestration-runs`

Classified as **B — compatibility API**, implemented as a facade:
`StartAsync` still creates a Pending row; `GetAsync`/`RequestCancellationAsync`
now read-project the live `WorkItem`/`AgentRun` state at call time
(`OrchestrationService.LoadWithLiveProjectionAsync`) rather than trusting a
persisted `OrchestrationRun.Status`. Nothing in this projection path calls
`UpdateAsync` — it reflects, never persists.

## 5. Classification of `AutonomousCodingOrchestrator`

Confirmed **fully retired**: every distinct responsibility it held (build/
test, review, evaluation, learning, repair-context construction, audit
trail) is now covered elsewhere in the AgentRun pipeline or in the newly
relocated `WorkItemCompletionProcessor`. No behavior was deleted without a
new home except the workspace-copy/task-planning steps in
`OrchestrationRunExecutionEngine` (removed per spec — the AgentRun pipeline
never did any workspace prep either; this was a pre-existing architectural
gap in the new pipeline, not something this phase introduced or is asked
to fix) and the per-attempt `OrchestrationRunStep` trace recording (see
Remaining Risks).

## 6. What Was Retired

Deleted (zero remaining production references, confirmed by grep):

- `AI.Factory.Application/Orchestration/AutonomousCodingOrchestrator.cs`
- `AI.Factory.Application/Abstractions/IAutonomousCodingOrchestrator.cs`
- `AI.Factory.Application/Abstractions/ITaskScheduler.cs`
- `AI.Factory.Application/Orchestration/WorkItemTaskScheduler.cs`
- `AI.Factory.Application/Abstractions/IHermesExecutionRepository.cs`
- `AI.Factory.Infrastructure/Persistence/HermesExecutionRepository.cs`
- `AI.Factory.Application/Abstractions/IValidationEngine.cs`
- `AI.Factory.Application/Orchestration/ValidationEngine.cs`

Test files deleted in full (each exercised only the deleted class(es), with
no remaining live invariant left untested):

- `AutonomousCodingOrchestratorTests.cs` — including its two Phase 18.12
  invariant tests (`ExecuteNextReadyTaskAsync_NeverTouchesProjectStatus_...`).
  Those invariants ("this orchestrator never touches Project.Status") are
  no longer at risk because there is no code path left to test — this is an
  intentionally-retired test file, not a silently-dropped invariant. The
  equivalent invariant for the live pipeline (`TaskDispatchScheduler`/
  `ProjectCompletionGate` own Project-lifecycle exclusively) is already
  covered by `ProjectCompletionGateTests.cs`/`ProjectCompletionIntegrationTests.cs`,
  untouched by this phase.
- `WorkItemTaskSchedulerTests.cs` — tested only the deleted `WorkItemTaskScheduler`.
- `ValidationEngineTests.cs` — tested only the deleted `ValidationEngine`.
- `OrchestrationInstrumentationTests.cs` — tested `AutonomousCodingOrchestrator`'s
  real per-stage `OrchestrationRunStep` production via a real
  `OrchestrationRunTraceService`; both the orchestrator and the step-tracking
  behavior it tested are gone (see Remaining Risks: empty `Trace`/`Steps`).

Not deleted, per explicit instruction (data/table stays, zero migration,
never written to again — same precedent as Phase 18.12's `WorkflowTransition`):

- `HermesExecution` entity class and its `DbSet`/table mapping in `FactoryDbContext`.

## 7. What Was Retained

- `ITaskStateManager`/`TaskStateManager` — shared, actively used by
  `AgentRunWorker`'s heartbeat, `WorkflowRecoveryHostedService`, and
  `CriticalServicesHealthCheck`.
- `IModelRouter`/`ModelRouter` — re-verified with `grep`: still has live
  production callers (`AgentCodeReviewer`, `ModelTaskPlanner`,
  `CriticalServicesHealthCheck`) independent of anything retired here. **Not**
  orphaned — corrected from the phase brief's caution list.
- `IOrchestrationRunTraceService`, `IOrchestrationProgressReporter` — kept
  per instruction even though `grep` confirms they now have zero remaining
  *callers* of their methods (only their own definition files and DI
  registrations remain) — orphaned-but-not-deleted, out of this phase's
  scope; flagged below as a remaining risk.
- `EvaluationEngine`'s dependency on `IApprovalRepository`/`ApprovalRequest`
  — intentionally left exactly as Phase 18.12 settled it (this phase does
  not touch that decision; re-verified unchanged file set via `grep`).

## 8. What Became a Compatibility Facade

- `OrchestrationRun` / `OrchestrationService` — `StartAsync` unchanged;
  `GetAsync`/`RequestCancellationAsync` are now a read-time projection over
  `WorkItem`/`AgentRun`, documented critical invariant: "OrchestrationRun
  does NOT execute the WorkItem itself."
- `OrchestrationRunExecutionEngine` — now "prepare and hand off" only.

## 9. AgentRun Integration (the new dispatch primitive)

`ITaskDispatchScheduler.DispatchWorkItemAsync(workItemId)` (new) reuses the
exact same atomic claim (`WorkItem.TransitionTo(Running)` + optimistic
`Version` token) and `AgentRun` creation as `RunCycleAsync`'s own
`DispatchOneAsync` — no second claim mechanism, verified by a passing test
(`DispatchWorkItemAsync_ReadyWorkItem_CreatesAgentRun_AndTransitionsWorkItemToRunning`)
plus the pre-existing race-safety tests for `RunCycleAsync` itself.

## 10. Cancellation / Timeout Mapping

- `POST /api/orchestration-runs/{id}/cancel` → `OrchestrationService.RequestCancellationAsync`
  → `IOrchestrationRunRepository.RequestCancellationAsync` (compatibility flag,
  unchanged) **and** `WorkItem.TransitionTo(Cancelled)` (the one real
  authority).
- Cooperative, not preemptive: matches the legacy engine's own semantics —
  neither ever interrupted an in-flight Hermes call, only excluded the
  WorkItem from future dispatch.
- Idempotent: a repeated cancel request on an already-Cancelled WorkItem is
  a no-op (guarded by `WorkItemTransitions.IsTerminal`), not an error.
- Race-safe: cancelling a WorkItem the AgentRun pipeline already completed
  raises `ConflictException` (run already terminal via projection) and
  never resurrects the WorkItem — proven by
  `OrchestrationServiceTests.RequestCancellationAsync_Throws_AndDoesNotResurrectWorkItem_WhenAgentRunAlreadyCompletedIt`
  and the API-level equivalent in `ApiIntegrationTests`.
- A cancelled WorkItem is excluded from all future `TaskDispatchScheduler`
  cycles by the existing Ready/Repairing status filter (no scheduler change
  needed) — proven by `RunCycleAsync_CancelledWorkItem_IsNeverRedispatched`.
- A late-finishing `AgentRun` cannot resurrect a Cancelled WorkItem —
  `AgentRunWorkItemSync`'s existing idempotency guard (WorkItem must still
  be Running) already covers this; proven directly by
  `SyncAsync_AlreadyCancelledWorkItem_NotResurrectedByLateFinishingAgentRun`.
- `AgentRun` still has no cancellation support (`CancellationRequestedAtUtc`)
  — intentional, unchanged, per the original design.
- Timeout: `AgentRunStatus.TimedOut` → `WorkItemStatus.Failed` (unchanged,
  one mapping, in `AgentRunWorkItemSync`). `OrchestrationRun`'s own status
  is a pure read-time projection of this — no second interpretation exists.

## 11. Verification / Repair Preservation

Unchanged and re-verified by the full existing repair/verification test
suite (`RepairVerificationLoopTests`, `RepairDispatchTests`,
`VerificationGateTests`, `ProjectCompletionGateTests`, all passing):
`IVerificationGate` remains the sole build/test authority;
`WorkItemStatus.Repairing` remains the sole recoverable-failure state,
re-dispatched through the existing bounded `RepairAttemptCount`/
`MaxRepairAttempts` budget. `WorkItemCompletionProcessor`'s review-block
path deliberately maps onto `Repairing` (not `Failed`) so a review block
re-enters this same bounded budget rather than inventing a second retry
counter.

## 12. EvaluationEngine / ApprovalRequest Decision

- Fixed the one in-scope defect: `EvaluationEngine.EvaluateAsync` computed
  `repairCount` from `WorkItem.AttemptCount` (a field the legacy
  `ITaskStateManager.TryStartAsync` path — dead once this phase lands —
  was the only writer of). Changed to `WorkItem.RepairAttemptCount`, the
  field the live `TaskDispatchScheduler.DispatchRepairAsync` actually
  increments. One-line fix to the same public contract (`EvaluationResult.RepairCount`),
  covered by an updated test (`EvaluateAsync_DerivesRepairCount_FromRepairAttemptCount`).
- `IApprovalRepository`/`ApprovalRequest` dependency: left exactly as Phase
  18.12 decided (no production writer of `ApprovalRequest` exists; the
  `humanApproval` metric in `EvaluationResult` continues to read an always-empty
  table and evaluates to `null`/fully-validated-by-default for that
  dimension). This phase did not re-litigate that decision — re-verified
  the reference set is unchanged (§Validation below).

## 13. API Compatibility

All five `/api/orchestration-runs*` endpoints preserved unchanged at the
routing layer:

```
POST   /api/orchestration-runs
GET    /api/orchestration-runs/{id}
GET    /api/orchestration-runs/{id}/attempts
GET    /api/orchestration-runs/{id}/logs
POST   /api/orchestration-runs/{id}/cancel
```

`GET .../{id}` now returns an empty `Trace` array for any run that has
dispatched past `Pending` (see Remaining Risks) — an accepted compatibility
tradeoff, not a bug; existing tests that manually seed `OrchestrationRunStep`
rows and read them back through the endpoint still pass unchanged (they
test the read/serialization path, not engine-produced steps).

## 14. Persistence / Schema Result

One additive, nullable migration:

- `apps/src/AI.Factory.Infrastructure/Persistence/Migrations/20260914113832_AddOrchestrationRunAgentRunId.cs`
  — adds `OrchestrationRun.AgentRunId` (`Guid?`, nullable `TEXT` column on
  `orchestration_runs`). No existing row is invalidated (defaults to `NULL`,
  meaning "not yet dispatched" — the same short-circuit branch
  `LoadWithLiveProjectionAsync` already needs for `Status == Pending`).
- `dotnet ef migrations has-pending-model-changes` reports **no pending
  changes** after this migration (see §Validation).
- No destructive schema change. `HermesExecution` table/entity retained,
  unused going forward, per instruction.

## 15. Focused Tests Added / Changed

- `WorkItemCompletionProcessorTests.cs` (new) — review-pass → Completed +
  ValidationRun/ReviewRun/EvaluationRun persisted + lesson recorded;
  review-blocks (Critical finding, and Medium-per-`ReviewOptions`) →
  Repairing with bounded findings summary, no EvaluationRun/lesson;
  evaluation-throws → still Completed, exception swallowed+logged.
- `TaskDispatchSchedulerTests.cs` — added
  `DispatchWorkItemAsync_ReadyWorkItem_CreatesAgentRun_AndTransitionsWorkItemToRunning`,
  `DispatchWorkItemAsync_NotReadyWorkItem_SkipsWithNotReadyReason_NoAgentRunCreated`,
  `DispatchWorkItemAsync_WorkItemNotFound_ReturnsWorkItemNotFoundReason`,
  `DispatchWorkItemAsync_NoMatchingAgentDefinition_SkipsWithoutClaimingWorkItem`,
  `RunCycleAsync_CancelledWorkItem_IsNeverRedispatched`.
- `OrchestrationRunExecutionEngineTests.cs` (rewritten) — dispatch-success
  (Executing + AgentRunId set), dispatch-skip → Failed (NotReady,
  NoAgentDefinition), cancellation-before-dispatch, project-not-found,
  workitem-not-found.
- `OrchestrationServiceTests.cs` — added cancellation-from-Ready/Running/Repairing,
  idempotent repeated cancellation, cancel-races-with-completion → Conflict +
  no resurrection, and a read-time-projection-never-persists test.
- `AgentRunWorkItemSyncTests.cs` — added a `FakeWorkItemCompletionProcessor`
  double (constructor now takes `IWorkItemCompletionProcessor`) and
  `SyncAsync_AlreadyCancelledWorkItem_NotResurrectedByLateFinishingAgentRun`.
- `ApiIntegrationTests.cs` — added
  `PostOrchestrationRunCancel_TransitionsWorkItemToCancelled` (incl.
  idempotent repeat) and
  `PostOrchestrationRunCancel_Returns409_AndDoesNotResurrectWorkItem_WhenAgentRunAlreadyCompletedIt`.
- `EvaluationEngineTests.cs` — replaced
  `EvaluateAsync_DerivesRepairCount_FromAttemptCountMinusOne` with
  `EvaluateAsync_DerivesRepairCount_FromRepairAttemptCount`.
- `WorkerCompositionRootTests.cs` — removed `ITaskScheduler`/`IValidationEngine`/
  `IAutonomousCodingOrchestrator` resolution assertions; added
  `ITaskDispatchScheduler`, `IProjectCompletionGate`, `IVerificationGate`,
  `IWorkItemCompletionProcessor`.
- `WorkItemExecutionIntegrationTests.cs`, `ProjectCompletionIntegrationTests.cs`,
  `RepairVerificationLoopTests.cs` — added a `FakeWorkItemCompletionProcessor`
  double to each file's `AgentRunWorkItemSync` construction site (mechanical
  constructor-signature update, no behavior change to what those files test).

## 16. DI / Registration Changes

- `AI.Factory.Api/Program.cs` and `AI.Factory.Worker/Program.cs`: removed
  registrations for `ITaskScheduler`, `IHermesExecutionRepository`,
  `IValidationEngine`, `IAutonomousCodingOrchestrator`; added
  `IWorkItemCompletionProcessor` → `WorkItemCompletionProcessor`.
- `AI.Factory.Api/Program.cs` additionally now registers `IVerificationGate`,
  `IProjectCompletionGate`, `ITaskDispatchScheduler` (previously Worker-only)
  — required so `CriticalServicesHealthCheck`'s new
  `CheckResolvable<ITaskDispatchScheduler>()` can actually resolve the
  service in the Api process; the Worker process still owns the real
  scheduling loop (`TaskSchedulerHostedService`). All of `TaskDispatchScheduler`'s
  transitive dependencies were already registered in Api.
- `CriticalServicesHealthCheck.CheckResolvable<IAutonomousCodingOrchestrator>()`
  → `CheckResolvable<ITaskDispatchScheduler>()`.

## 17. Validation

```
dotnet build apps/AI.Factory.slnx -c Debug     → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet build apps/AI.Factory.slnx -c Release   → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    AI.Factory.Worker.Tests.dll      : 84 passed,  0 failed,  0 skipped
    AI.Factory.Application.Tests.dll : 707 passed, 0 failed,  0 skipped
    AI.Factory.Api.Tests.dll         : 293 passed, 0 failed,  0 skipped
    TOTAL: 1084 passed, 0 failed, 0 skipped (two initially-flaky, timing-
    sensitive Api tests unrelated to this phase — DatabaseResiliencePolicyTests
    backoff-cancellation timing, ProjectApprovalApiTests concurrent-request
    503 — passed in isolation and on the confirming full re-run; not caused
    by, or related to, any change in this phase)

dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .
    → "No changes have been made to the model since the last migration."
```

Repository-wide re-searches (post-implementation):

```
grep -rn "AutonomousCodingOrchestrator" apps/src apps/tests
    → 7 hits, all doc-comment prose in still-live files referencing the
      retired class historically (IOrchestrationProgressReporter.cs,
      TaskDispatchScheduler.cs, WorkItemCompletionProcessor.cs,
      WorkItemService.cs, WorkItem.cs, CircuitBreakerAgentExecutorDecorator.cs,
      HermesAgentRuntime.cs) — zero code references, zero remaining type.

grep -rn "OrchestrationRunWorker\|OrchestrationRunExecutionEngine" apps/src apps/tests
    → All hits are the (unchanged) OrchestrationRunWorker background service,
      the rewritten OrchestrationRunExecutionEngine class/registration/tests,
      and doc-comment cross-references. No dangling references.

grep -rn "ExecuteSpecificWorkItemAsync\|ExecuteNextReadyTaskAsync" apps/src apps/tests
    → 0 hits (fully removed).

grep -rn "ApprovalRequest\|IApprovalRepository" apps/src apps/tests
    → Same file set as before this phase's work (EvaluationEngine.cs,
      IApprovalRepository.cs, ApprovalRequest.cs, ApprovalRepository.cs,
      Program.cs x2, migrations, OperationalDiagnosticsService.cs,
      DatabaseConcurrencyTests.cs, EvaluationEngineTests.cs) — unchanged
      from Phase 18.12, as required.
```

## 18. Migration Status

`20260914113832_AddOrchestrationRunAgentRunId` — additive, nullable,
applied to the model snapshot; `has-pending-model-changes` confirms clean.

## 19. Remaining Risks (explicitly not addressed — out of this phase's scope)

- `IOrchestrationRunTraceService`/`IOrchestrationProgressReporter` are now
  orphaned (registered, implemented, but no remaining production caller of
  their methods after `OrchestrationRunExecutionEngine`'s rewrite). Left in
  place per instruction; a future phase should decide whether to delete
  them or find them a new caller.
- `GET /api/orchestration-runs/{id}` returns an empty `Trace` array for
  every run going forward (no more `OrchestrationRunStep` rows are ever
  written) — a real, visible behavior change for any external consumer of
  that field, accepted as a documented compatibility tradeoff per the
  target architecture.
- Cancellation remains cooperative, not preemptive — an in-flight Hermes
  call is never actually interrupted, only excluded from future work. This
  is unchanged from the legacy engine's own real behavior, not a regression.
- `HermesExecution` entity/table remains mapped and permanently unused
  (matches the `WorkflowTransition` precedent from Phase 18.12) — a future
  phase could drop it with a real migration once it's confirmed safe.
- `HermesAgentRuntime.ExecuteAsync` still passes `WorkingDirectory: string.Empty`
  to `IAgentExecutor` (pre-existing gap, explicitly out of scope, not
  touched here).

---

```
WorkItem Execution Unification: VERIFIED
Final Decision: GO
```
