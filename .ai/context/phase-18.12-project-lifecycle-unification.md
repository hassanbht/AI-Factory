# Phase 18.12 — Project Lifecycle Unification & Legacy Orchestration Retirement

## 1. Executive Summary

Phase 18.11 made `HumanReview -> Completed` the sole authoritative completion boundary for the *new* Project lifecycle (`ProjectService.ApproveAsync`), but left a second, structurally independent Project-transition pipeline in place: `ApprovalService` / `IWorkflowEngine` / `WorkflowEngine` / `IWorkflowStateStore` / `WorkflowStateStore` / `IWorkflowTransitionValidator` / `WorkflowTransitionValidator`, driven from `AutonomousCodingOrchestrator` (via the `/api/orchestration-runs` feature) and from the public `/api/approvals/*` endpoints.

Empirical dependency analysis (full-repository search, not assumption) showed this second pipeline was **not dead code** — it was live, wired into both `AI.Factory.Api` and `AI.Factory.Worker`, and was the only thing letting a Project jump `Implementation -> HumanReview` and complete via a route other than `ProjectService.ApproveAsync`. This phase retired that pipeline's Project-lifecycle authority entirely (the **Preferred** resolution from the phase spec), while preserving every non-Project-lifecycle capability that turned out to depend on adjacent, still-legitimate pieces (`ApprovalRequest`/`IApprovalRepository`, consumed by `EvaluationEngine`; `AutonomousCodingOrchestrator`'s actual WorkItem-execution logic, consumed by `OrchestrationRunExecutionEngine`/`OrchestrationRunWorker`/`POST /api/orchestration-runs`).

**Result:** one Project lifecycle, one transition table (`ProjectTransitions`), one completion authority (`ProjectService.ApproveAsync`), zero schema changes, zero remaining runtime `Project.Status =` assignment outside the authoritative path (verified by repository-wide search — see §8).

## 2. Repository Inspection / Dependency Findings

Full-repository search (`WorkflowStateStore|WorkflowTransitionValidator|IWorkflowEngine|ApprovalService|AutonomousCodingOrchestrator|ApprovalRequest`) plus targeted reads of every match found:

| Component | Only consumer(s) before this phase |
|---|---|
| `IWorkflowEngine` / `WorkflowEngine` | `ApprovalService` (constructor dependency) — no other consumer anywhere in `src/` |
| `IWorkflowStateStore` / `WorkflowStateStore` | `WorkflowEngine` only. **This class was the direct `project.Status = transition.ToStatus;` mutator** — the literal second lifecycle authority the phase spec targets |
| `IWorkflowTransitionValidator` / `WorkflowTransitionValidator` | `WorkflowEngine` only. Its `CanTransition` allowed `to == HumanReview` unconditionally **from any non-terminal status**, and `to == Completed` only when `from == HumanReview` (its own `LinearSequence` array, structurally independent from `ProjectTransitions`) |
| `ApprovalService` | `AutonomousCodingOrchestrator` (2 call sites, both production) + `/api/approvals/pending`, `/api/approvals/{id}/approve`, `/api/approvals/{id}/reject` (Api `Program.cs`) |
| `AutonomousCodingOrchestrator` | `OrchestrationRunExecutionEngine.ExecuteAsync` → `IAutonomousCodingOrchestrator.ExecuteSpecificWorkItemAsync`, itself driven by `OrchestrationRunWorker`'s background poll loop and reachable via `POST /api/orchestration-runs`. **Confirmed still live and load-bearing** — not a dead legacy path |
| `ApprovalRequest` (entity) / `IApprovalRepository` / `ApprovalRepository` | `ApprovalService` (write path, retired) **and** `EvaluationEngine.EvaluateAsync` (`GetLatestForProjectAsync`, read-only, feeds the `humanApproval` evaluation metric) — a genuine non-Project-lifecycle consumer |
| `WorkflowTransition` (entity/table) | Written only by `WorkflowStateStore` (retired); never read by any API or service |

`AutonomousCodingOrchestrator`'s two Project-lifecycle call sites, exactly:
1. `HandleFailureAsync`: on repair-attempt exhaustion with `RepairOptions.Policy == RepairExhaustionPolicy.HumanReview`, called `_approvalService.RequestApprovalAsync(...)` → `WorkflowEngine` → `WorkflowStateStore` writes `project.Status = HumanReview` directly, from whatever status the Project was in (typically `Implementation`) — **the exact `Implementation -> HumanReview` bypass §7/§12 of the phase spec forbids**.
2. `MaybeRequestFinalApprovalAsync`: on a WorkItem's successful completion (build+test+review all pass), if all of the Project's WorkItems were terminal, called the same `RequestApprovalAsync` path to push the Project straight to `HumanReview` — bypassing the new pipeline's `Testing -> Reviewing` steps entirely.

## 3. Classification of Every Legacy Component

| Component | Classification | Disposition |
|---|---|---|
| `IWorkflowEngine` / `WorkflowEngine` | **A. Project-lifecycle obsolete** | Deleted |
| `IWorkflowStateStore` / `WorkflowStateStore` | **A. Project-lifecycle obsolete** | Deleted |
| `IWorkflowTransitionValidator` / `WorkflowTransitionValidator` | **A. Project-lifecycle obsolete** | Deleted |
| `ApprovalService` | **A. Project-lifecycle obsolete** | Deleted |
| `/api/approvals/pending`, `/api/approvals/{id}/approve`, `/api/approvals/{id}/reject` | **A. Project-lifecycle obsolete** | Removed (404 now; proven by test) |
| `ApprovalDecisionRequest` DTO | **A. Project-lifecycle obsolete** | Deleted (only used by the removed endpoints) |
| `AutonomousCodingOrchestrator`'s two `RequestApprovalAsync` call sites | **A. Project-lifecycle obsolete** | Removed from the class; the class itself is **not** obsolete (see below) |
| `AutonomousCodingOrchestrator` (WorkItem execution: build/test/review/repair/evaluation/learning) | **B. Still required for non-Project functionality** | Unchanged — still the execution engine behind `OrchestrationRunExecutionEngine`/`OrchestrationRunWorker`/`POST /api/orchestration-runs` |
| `ApprovalRequest` entity, `IApprovalRepository` / `ApprovalRepository` | **B. Still required for non-Project functionality** | Unchanged — `EvaluationEngine.EvaluateAsync` genuinely depends on `GetLatestForProjectAsync` |
| `WorkflowTransition` entity/table | **B (data only)** | Kept mapped in `FactoryDbContext` (zero migration); no longer written by anything, table stays empty going forward |
| `ValidationRun`, `ReviewRun` | **B. Still required for non-Project functionality** | Untouched — these are WorkItem-level records written by `AutonomousCodingOrchestrator`'s still-active execution path, unrelated to Project-lifecycle authority; out of this phase's scope |

No component required **C (adapt)** or **D (temporary compatibility)** — the "Second-best" adapter path was considered and explicitly rejected for `ApprovalService`: redirecting `RequestApprovalAsync` into `ProjectService`/`ProjectTransitions` would have required loosening `ProjectTransitions` to accept `X -> HumanReview` from arbitrary non-terminal statuses, which is precisely the invariant this phase must *prevent*, not preserve behind a different class name. The correct fix was removing the call sites, not relocating them.

## 4. What Was Retired

- `apps/src/AI.Factory.Application/Approvals/ApprovalService.cs`
- `apps/src/AI.Factory.Application/Orchestration/WorkflowEngine.cs`
- `apps/src/AI.Factory.Application/Orchestration/WorkflowTransitionValidator.cs`
- `apps/src/AI.Factory.Infrastructure/Persistence/WorkflowStateStore.cs`
- `apps/src/AI.Factory.Application/Abstractions/IWorkflowEngine.cs`
- `apps/src/AI.Factory.Application/Abstractions/IWorkflowStateStore.cs`
- `apps/src/AI.Factory.Application/Abstractions/IWorkflowTransitionValidator.cs`
- DI registrations for all of the above in `AI.Factory.Api/Program.cs` and `AI.Factory.Worker/Program.cs`
- `/api/approvals/pending`, `/api/approvals/{id}/approve`, `/api/approvals/{id}/reject` (`AI.Factory.Api/Program.cs`) and the `ApprovalDecisionRequest` DTO
- `AutonomousCodingOrchestrator`'s `ApprovalService` dependency, its `MaybeRequestFinalApprovalAsync` method, and its repair-exhaustion `HumanReview`-escalation block in `HandleFailureAsync`
- Tests proving only retired behavior: `ApprovalServiceTests.cs`, `WorkflowEngineTests.cs`, `WorkflowTransitionValidatorTests.cs`, `WorkflowStateStoreResumeTests.cs` (deleted in full)

## 5. What Was Adapted

Nothing required adaptation in the "wrap one state machine with another" sense the spec warns against. The one behavior-preserving adjustment: 5 unrelated infrastructure tests (rate limiting, security headers, correlation-ID isolation, load/burst) used `GET /api/approvals/pending` purely as a cheap, parameter-free, always-200 "some business endpoint" stand-in. They were repointed to `GET /api/projects` (equally cheap, equally param-free, still exists) — a mechanical fix with no behavioral significance to the lifecycle itself.

## 6. What Remains and Why

- **`AutonomousCodingOrchestrator`** — remains, minus its Project-lifecycle side effects. It is still the real execution engine for the on-demand single-WorkItem `OrchestrationRun` feature (`POST /api/orchestration-runs`, polled by `OrchestrationRunWorker`), which is a genuinely separate, still-used capability from the new pipeline's `TaskDispatchScheduler`/`AgentRunWorker`. Retiring it outright would have deleted a live feature outside this phase's scope ("Do not rewrite its entire implementation").
- **`ApprovalRequest` entity, `IApprovalRepository`/`ApprovalRepository`** — remain unchanged. `EvaluationEngine.EvaluateAsync` reads `GetLatestForProjectAsync` to populate its `humanApproval` evaluation metric. This is a read-only, non-authoritative consumer; keeping it avoids an out-of-scope rewrite of `EvaluationEngine`.
- **`WorkflowTransition` entity/table** — remains mapped in `FactoryDbContext` (zero migration) but is now permanently unwritten; historical rows (if any existed) are preserved, no destructive schema change.

## 7. Final Project Lifecycle Authority

```
ProjectService
     |
     v
ProjectTransitions  (Project.TransitionTo)
     |
     +-- TaskDispatchScheduler -> ProjectCompletionGate   (automatic: Implementation -> Testing -> Reviewing -> HumanReview)
     |
     +-- ProjectService.ApproveAsync                       (explicit human action: HumanReview -> Completed)
     |
     v
Project.Status
```

`ProjectTransitions.cs` was **not modified** in this phase — Phase 18.11's guard table (no `Reviewing -> Completed`, `HumanReview -> Completed` the sole completion edge) already matched the target architecture; Phase 18.12's job was removing every other component capable of writing `Project.Status` outside it.

## 8. Evidence That Direct Project.Status Mutation Is Controlled

Repository-wide search after implementation, pattern `\.Status\s*=\s*(ProjectStatus\.|status\b|transition\.ToStatus)`, across `apps/src` **and** `apps/tests`:

```
apps/src            -> 0 matches
apps/tests/AI.Factory.Application.Tests/ProjectServiceTests.cs           -> test seeding (Arrange step, via ProjectRepository.SaveChangesAsync)
apps/tests/AI.Factory.Application.Tests/ProjectCompletionGateTests.cs    -> test seeding (Arrange step)
apps/tests/AI.Factory.Application.Tests/OrchestrationRunExecutionEngineTests.cs -> test double simulating an external HumanReview transition to exercise OrchestrationRunExecutionEngine's own reaction, not asserting AutonomousCodingOrchestrator behavior
```

**Zero** production/`src` code paths assign `Project.Status` directly. The only three remaining assignments are narrowly-scoped test-seeding helpers (`[Arrange]`-step direct field sets against an in-memory SQLite `DbContext`, a pre-existing and already-reviewed pattern from Phases 18.2–18.11), exactly the exception §5 of the phase spec anticipates and permits.

## 9. Evidence That Duplicate Transition Semantics Are Removed

`WorkflowTransitionValidator` (the second, independently-maintained `ProjectStatus` state machine) no longer exists. `ProjectTransitions` (`apps/src/AI.Factory.Domain/Entities/ProjectTransitions.cs`) is the sole `IsValid`/`IsTerminal` authority in the codebase — confirmed by the same repository-wide search finding no other type implementing transition-adjacency logic over `ProjectStatus`.

## 10. API Compatibility Result

| Endpoint | Before | After |
|---|---|---|
| `GET /api/approvals/pending` | 200, legacy list | **Removed — 404** |
| `POST /api/approvals/{id}/approve` | 200/404/409, legacy completion path | **Removed — 404** |
| `POST /api/approvals/{id}/reject` | 200/404/409, legacy failure path | **Removed — 404** |
| `POST /api/projects/{id}/approve` | (Phase 18.11) sole completion boundary | Unchanged |
| `PATCH /api/projects/{id}/status` | (Phase 18.11) rejects `Completed` | Unchanged |
| `POST /api/orchestration-runs`, `GET .../{id}`, `.../attempts`, `.../logs`, `.../cancel` | Live | Unchanged — `AutonomousCodingOrchestrator`'s WorkItem-execution behavior is untouched |

Removal of `/api/approvals/*` is proven, not merely asserted: `ApiIntegrationTests.LegacyApprovalsEndpoint_NoLongerExists_Returns404`.

## 11. Data / Schema Result

**Zero migration.** `dotnet ef migrations has-pending-model-changes` reports "No changes have been made to the model since the last migration" both before and after this phase's changes. `ApprovalRequest` and `WorkflowTransition` remain mapped `DbSet`s in `FactoryDbContext` — no destructive deletion of persisted data or schema, per §11 of the phase spec.

## 12. Focused Tests Added / Changed

- **Retired-behavior deletion**: `ApprovalServiceTests.cs`, `WorkflowEngineTests.cs`, `WorkflowTransitionValidatorTests.cs`, `WorkflowStateStoreResumeTests.cs` — deleted; they exercised only the now-deleted classes.
- **Authority tests** (new, in `AutonomousCodingOrchestratorTests.cs`): `ExecuteNextReadyTaskAsync_NeverTouchesProjectStatus_WhenAttemptsExhausted_RegardlessOfPolicy` and `ExecuteNextReadyTaskAsync_NeverTouchesProjectStatus_WhenAllWorkItemsCompleted` replace the two tests that asserted the retired escalation behavior — both now assert `Project.Status` stays `Implementation` and both `db.ApprovalRequests`/`db.WorkflowTransitions` stay empty, regardless of `RepairOptions.Policy` or WorkItem completion.
- **API regression** (new, in `ApiIntegrationTests.cs`): `LegacyApprovalsEndpoint_NoLongerExists_Returns404` proves the retired route is gone.
- **Composition-root regression**: `WorkerCompositionRootTests.cs` — removed the two `[InlineData]` rows for the deleted `IWorkflowEngine`/`ApprovalService` types.
- **Mechanical fixes** (constructor signature change, not behavioral): `OrchestrationInstrumentationTests.cs` (second `AutonomousCodingOrchestrator` construction site), `OrchestrationRunExecutionEngineTests.cs` (stale comment referencing the deleted `MaybeRequestFinalApprovalAsync`, corrected — no assertion changes).
- **Unrelated infra tests repointed** (not lifecycle-related): `SecurityHeadersTests.cs`, `OperationalEndpointSecurityTests.cs`, `RateLimitingTests.cs`, `Load/CorrelationIsolationLoadTests.cs`, `Load/RateLimitBurstStressTests.cs` — swapped their stand-in "cheap business GET" from the removed `/api/approvals/pending` to `/api/projects`.

## 13. Full Build/Test Results

```
dotnet build AI.Factory.slnx -c Debug     -> Build succeeded. 0 Warning(s). 0 Error(s).
dotnet build AI.Factory.slnx -c Release   -> Build succeeded. 0 Warning(s). 0 Error(s).

dotnet test AI.Factory.slnx -c Debug --no-build:
  AI.Factory.Worker.Tests.dll      -> Passed:  83, Failed: 0, Skipped: 0, Total:  83
  AI.Factory.Application.Tests.dll -> Passed: 726, Failed: 0, Skipped: 0, Total: 726
  AI.Factory.Api.Tests.dll         -> Passed: 291, Failed: 0, Skipped: 0, Total: 291
  TOTAL                            -> Passed: 1100, Failed: 0
```

(The first full run after the endpoint/class removals surfaced 7 pre-existing failures in unrelated infra tests that depended on `/api/approvals/pending` as a generic stand-in endpoint — see §12; all 7 fixed by repointing to `/api/projects`, confirmed green on rerun.)

## 14. Migration Status

```
dotnet ef migrations has-pending-model-changes --startup-project ../AI.Factory.Api --project .
-> No changes have been made to the model since the last migration.
```

## 15. Remaining Architectural Risks

- `EvaluationEngine`'s `humanApproval` metric reads `IApprovalRepository.GetLatestForProjectAsync`, which — since nothing in production creates `ApprovalRequest` rows anymore — will now always return `null` going forward (evaluated as `is null || Approved`, i.e. always passes this specific check). This was already a near-vacuous check in practice before this phase (evaluation runs on a WorkItem's own completion, before that same completion could trigger an approval request), so behavior here is not a regression, but the field is now structurally dead weight. Left as-is: `EvaluationEngine` was explicitly out of scope for this phase, and the metric fails safe (never blocks), not unsafe.
- `AutonomousCodingOrchestrator`/`OrchestrationRun` remains a second, independent WorkItem-execution path alongside the new `TaskDispatchScheduler`/`AgentRunWorker` pipeline — not a Project-lifecycle authority anymore (this phase's mandate), but still an architecturally separate execution engine. Full unification of WorkItem execution itself was explicitly out of scope (§8 of the phase spec: "Do not rewrite its entire implementation") and is a candidate for a future phase if the two execution paths should also be consolidated.
- `RepairOptions.Policy`/`RepairExhaustionPolicy.HumanReview` is now inert configuration inside `AutonomousCodingOrchestrator` (the branch that read it was removed). Left in place rather than deleted to avoid unrelated config-surface churn outside this phase's explicit scope; a future cleanup could remove the now-meaningless enum value.

## 16. Final Decision

All invariants in §12 of the phase spec hold, verified empirically rather than asserted:
- Single Project lifecycle, single transition table (`ProjectTransitions`), single completion authority (`ProjectService.ApproveAsync`) — confirmed by full-repository search finding no remaining second implementation.
- No agent, scheduler, legacy workflow, or orchestrator can directly produce `ProjectStatus.Completed` — the only remaining `Completed`-producing code path is `ProjectService.ApproveAsync`, gated on `HumanReview`.
- No active path can jump `Implementation -> HumanReview` — the code path that did so (`AutonomousCodingOrchestrator` → `ApprovalService`) was removed, not merely documented.
- Zero schema changes; zero destructive data deletion.
- Full build/test suite green.

```
Project Lifecycle Unification: VERIFIED
Final Decision: GO
```
