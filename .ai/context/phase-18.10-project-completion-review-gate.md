# Phase 18.10 — Deterministic Project Completion & Review Gate

## 1. Executive Summary

Phase 18.9 closed the WorkItem-level gap: a WorkItem's "completed" self-report is
independently re-verified (build/test) before it may reach `Completed`, and a
verification failure routes through a bounded, deterministic repair loop.

Phase 18.10 closes the equivalent gap at the **Project** level. Before this
phase, nothing in the active pipeline (`TaskDispatchScheduler`,
`AgentRunWorkItemSync`, the hosted services) ever looked at a Project's
WorkItems *as a whole* to decide whether the Project itself was done. A
Project could sit in `Implementation` forever after all of its WorkItems
completed, with no mechanism to progress it toward human review.

This phase adds a new, small, deterministic `IProjectCompletionGate` that:

- recognizes "all required WorkItems Completed, no active AgentRuns" and
  advances `Implementation -> Testing`;
- runs the Factory's own build/test executors against the whole
  `Project.RootPath` (via a generalized `IVerificationGate.VerifyProjectAsync`)
  and advances `Testing -> Reviewing` on success, or fails the Project closed
  (`-> Failed`/`-> Cancelled`) otherwise — **no new Project-wide repair loop**;
- re-confirms the same completion facts and advances `Reviewing -> HumanReview`;
- **never** transitions a Project to `Completed` itself — that remains the
  existing, explicit `ProjectService.TransitionStatusAsync` /
  `PATCH /api/projects/{id}/status` action.

The gate is wired into the existing `TaskDispatchScheduler.RunCycleAsync` (no
second scheduler), reuses the existing `ProjectTransitions` guard table
unchanged, and required exactly one additive schema field
(`Project.CurrentError`).

## 2. Repository Discovery

Inspected before writing any code (via direct reads and one Explore-agent
research pass):

- `AGENTS.md`; Phase 18.2/18.5/18.6/18.7/18.8/18.9 reports.
- `Project`, `ProjectStatus`, `ProjectTransitions`, `IProjectRepository` /
  `ProjectRepository`, `ProjectService`.
- `WorkItem`, `WorkItemStatus`, `WorkItemTransitions`, `WorkItemDependency`,
  `IWorkItemRepository` / `WorkItemRepository`.
- `TaskDependencyResolver`, `TaskDispatchScheduler`, `ITaskDispatchScheduler`.
- `AgentRun`, `AgentRunStatus`, `AgentRunTransitions`, `IAgentRunRepository` /
  `AgentRunRepository`, `AgentRunWorker`, `AgentRunWorkItemSync`.
- `IVerificationGate` / `VerificationGate`, `IBuildExecutor`/`ITestExecutor`,
  `ExecutionLimitsOptions`.
- Legacy `ValidationEngine`, `ValidationRun`, `ReviewRun`,
  `AutonomousCodingOrchestrator`, `ApprovalRequest`/`ApprovalService`,
  `IWorkflowEngine`/`WorkflowEngine`/`WorkflowStateStore`/
  `WorkflowTransitionValidator`.
- Existing Project lifecycle tests: `ProjectTransitionsTests`,
  `ProjectServiceTests`, `ProjectsAndTasksApiTests`, `WorkflowEngineTests`,
  `ApprovalServiceTests`.
- `TaskSchedulerHostedService`, `WorkflowRecoveryHostedService`.
- Existing Project API surface in `AI.Factory.Api/Program.cs`.

Key discovery: **two parallel, independent orchestration/transition
pipelines already coexist** in this codebase —

1. The new pipeline (Phase 18.3+): `TaskDispatchScheduler` /
   `AgentRun` / `AgentOrchestrator` / `AgentRunWorkItemSync` /
   `IVerificationGate`, with Project status guarded by
   `ProjectTransitions`/`Project.TransitionTo` via `ProjectService`.
2. The legacy pipeline: `AutonomousCodingOrchestrator` / `ITaskScheduler` /
   `ITaskStateManager` / `IValidationEngine` / `ValidationRun` / `ReviewRun`,
   with Project status guarded by a **different** table
   (`WorkflowTransitionValidator`) via `IWorkflowEngine`/`WorkflowStateStore`,
   reachable through `ApprovalService` and `/api/approvals/*`.

`AutonomousCodingOrchestrator.MaybeRequestFinalApprovalAsync` already contains
a "check all WorkItems for a project are terminal, then request approval"
pattern — but it belongs entirely to the legacy pipeline (its own
`ValidationRun`/keyword-matched build/test, `IWorkflowEngine`'s own transition
table). Per CLAUDE.md ("do not make the new Project lifecycle dependent on
the old autonomous orchestration pipeline") and the spec's own §24, this
phase does **not** reuse or merge with that legacy path — it builds the new,
equivalent capability entirely inside the Phase 18.3+ pipeline, using
`ProjectTransitions`/`Project.TransitionTo` as the only guard.

## 3. Existing Infrastructure Reused

- `ProjectTransitions` — **unchanged**. Every transition this phase needs
  (`Implementation -> Testing`, `Testing -> Reviewing`, `Testing -> Failed`,
  `Testing -> Cancelled`, `Reviewing -> HumanReview`) already existed in the
  table from Phase 18.2. No domain change was required here (unlike Phase
  18.9, which had to add `Repairing -> Running`).
- `Project.TransitionTo` — the only way `ProjectCompletionGate` ever mutates
  `Project.Status`, so every transition is still guarded, timestamped, and
  throws `InvalidProjectTransitionException` on a bad move.
- `IProjectRepository`/`ProjectRepository` — reused as-is (`GetByIdAsync`'s
  change-tracker-reload pattern, `SaveChangesAsync`).
- `IWorkItemRepository.GetByProjectIdAsync` — reused as-is to fetch the
  Project's WorkItems once per scheduler cycle (already loaded by
  `TaskDispatchScheduler` for readiness/dispatch — no second query needed by
  the gate itself).
- `IBuildExecutor`/`ITestExecutor`/`IProcessRunner`/`CommandPolicyOptions` —
  fully reused, unchanged. Both executors already take a plain
  `string ProjectPath`, so `Project.RootPath` passes straight through with no
  new process-execution abstraction.
- `ExecutionLimitsOptions` (`BuildTimeoutMinutes`/`TestTimeoutMinutes`) —
  reused unchanged for Project-level verification timeouts (same limits as
  WorkItem-level verification; no new config surface).
- `TaskDispatchScheduler`/`TaskSchedulerHostedService` — the existing
  scheduler and its existing periodic sweep are the sole integration point;
  no second scheduler, no second hosted service.
- `ConcurrencyConflictException`/`Version` (`IVersionedEntity`) — the only
  race-safety mechanism used; no locks, no ownership maps.
- `ProjectService.TransitionStatusAsync` / `PATCH /api/projects/{id}/status`
  — the existing, unmodified mechanism that remains the only way a Project
  reaches `Completed` from `HumanReview`.

## 4. Exact Gap Before Phase 18.10

Confirmed by direct code reading (and cross-checked by an Explore-agent
research pass): **nothing** in the active (non-legacy) pipeline ever computed
"are all of this Project's WorkItems Completed" and reacted to it.
`TaskDispatchScheduler.RunCycleAsync` only ever dispatched Ready/Repairing
WorkItems; `AgentRunWorkItemSync` only ever looked at the one WorkItem tied to
the terminal AgentRun it was syncing; `TaskSchedulerHostedService` only
filtered which Projects to run a scheduling cycle for. A Project could
therefore complete every one of its WorkItems and simply sit in
`Implementation` indefinitely, with no deterministic path toward
`Testing`/`Reviewing`/`HumanReview`.

## 5. Project Completion Eligibility Rules

"Required WorkItem" (§5 of the spec): the current `WorkItem` model has **no**
optional-vs-required concept (no field distinguishes them). Per the spec's
own instruction ("If no such concept exists, treat all WorkItems belonging to
the Project as required"), every WorkItem returned by
`IWorkItemRepository.GetByProjectIdAsync` is treated as required.

The eligibility rule (`ProjectCompletionGate.AllRequiredWorkItemsCompleted`):

```csharp
workItems.Count > 0 && workItems.All(x => x.Status == WorkItemStatus.Completed.ToString());
```

This single check simultaneously satisfies:

- **Completion**: only `WorkItemStatus.Completed` counts — Ready/Running/
  Testing/Repairing/Blocked/Failed/Cancelled all fail the check (covered by 7
  `[Theory]` cases in `ProjectCompletionGateTests`).
- **Dependency semantics** (§4): a dependency chain A -> B can only have B
  reach `Completed` after A already did (enforced by the existing, unmodified
  `TaskDependencyResolver`/scheduler dispatch order) — so "all WorkItems
  Completed" already implies the dependency graph is fully satisfied, without
  re-running `ITaskDependencyResolver` a second time inside this gate. This is
  a deliberate reuse decision, not an oversight — documented directly in
  `ProjectCompletionGate`'s class-level doc comment.
- **Empty Project** (§15): zero WorkItems makes `Count > 0` false, so an empty
  Project is **never** eligible — explicit fail-closed, covered by
  `EmptyProject_UsesExplicitFailClosedPolicy`.

A "valid plan exists" precondition (also raised in §5/§10) was deliberately
**not** added as a separate check: nothing in the existing
`ProjectTransitions`/`ProjectService` lifecycle already requires a persisted
plan for any transition (confirmed by reading `ProjectService.TransitionStatusAsync`
— it has zero extra business rules beyond `ProjectTransitions.IsValid`), so
inventing one here would be a new rule, not a reused one. The empty-Project
fail-closed check above already covers the degenerate "no plan, no work"
case.

## 6. Project Testing Gate

`IProjectCompletionGate.EvaluateAsync(Project, IReadOnlyList<WorkItem>, CancellationToken)`
— a single deterministic entry point, switching on `project.Status`:

| Current Status | Action |
|---|---|
| `Implementation` | If all WorkItems Completed and no active AgentRun → `Testing`. |
| `Testing` | Run Project-level verification → `Reviewing`/`Failed`/`Cancelled`. |
| `Reviewing` | If all WorkItems Completed and no active AgentRun → `HumanReview`. |
| anything else | No-op (`ProjectCompletionGateAction.None`). |

It never executes arbitrary LLM/agent commands, never reads
`AgentRun.Output`/`StructuredResult`, and never calls `TransitionTo(Completed)`
under any input.

## 7. Project Verification

`IVerificationGate` (Phase 18.8) was generalized rather than duplicated:
research confirmed its existing `VerifyAsync(Project, WorkItem, ...)` already
only reads `project.RootPath` for the actual build/test execution — the
`WorkItem` parameter was used solely for log-line context. A second interface
method was added:

```csharp
Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default);
```

`VerificationGate`'s implementation now shares one private `RunAsync(Project,
Guid? workItemId, CancellationToken)` between both public methods —
byte-for-byte the same build/test logic, bounded output, and
Passed/Failed/InfrastructureError/TimedOut/Cancelled classification as
before, just parameterized on an optional log-context id. No new
`IProcessRunner`/build/test abstraction, no LLM-suppliable command, no use of
`Project.Description` as a command — only `Project.RootPath`, exactly the
same trusted path Phase 18.8 already used.

## 8. Project Reviewing Gate

`Reviewing -> HumanReview` re-confirms the same two facts already required
for `Testing` entry (all WorkItems Completed, no active AgentRun) — a
defensive re-check, not a new kind of gate. No code-quality/architecture/
security/UX review is claimed or implied; the deterministic build/test run in
§7 is the only verification this phase performs, and the Reviewing gate is
explicit about checking only persisted WorkItem/AgentRun state, matching
what §10 of the spec allows.

## 9. HumanReview Boundary

`ProjectCompletionGate` only ever advances as far as `HumanReview`. Per
`ProjectTransitions`, `Reviewing -> Completed` is technically a *legal* table
entry (pre-existing, used only by the manual
`PATCH /api/projects/{id}/status` override) — but `ProjectCompletionGate`
itself never takes that edge; its `Reviewing` case only ever produces
`HumanReview`. `HumanReview -> Completed` remains exactly what it was before
this phase: an explicit call to `ProjectService.TransitionStatusAsync` (or
the equivalent `PATCH` endpoint), verified directly in the mandatory
end-to-end test.

## 10. Completion Policy

No code path added by this phase can produce `ProjectStatus.Completed`.
Verified explicitly by `ProjectCannotBecomeCompletedFromAgentSuccess` (drives
the gate through `Implementation -> Testing -> Reviewing -> HumanReview` via
four repeated evaluations and asserts it stops at `HumanReview`) and by the
end-to-end test (three successful AgentRuns + three Completed WorkItems +
passed Project verification still stops at `HumanReview`).

## 11. Active AgentRun Policy

Added `IAgentRunRepository.HasActiveRunsForProjectAsync(Guid, CancellationToken)`,
implemented as:

```csharp
_db.AgentRuns.AnyAsync(x => x.ProjectId == projectId && !TerminalAgentRunStatuses.Contains(x.Status), ...)
```

`TerminalAgentRunStatuses` (`Succeeded`/`Failed`/`TimedOut`/`Cancelled`) is
duplicated from `AgentRunTransitions.TerminalStatuses` rather than calling it
— EF Core cannot translate a call to an external static method inside an
`AnyAsync` predicate into SQL. The exact same tradeoff already exists in this
codebase (`TaskSchedulerHostedService.TerminalProjectStatuses`), so this is a
consistent, not novel, pattern. Checked both at `Testing`-entry and at
`Reviewing -> HumanReview` (defense in depth — the spec ties it explicitly to
the review boundary in §16, but applying it slightly earlier too is harmless
and safer).

## 12. Dependency Semantics

Covered in §5 above: the gate's single "all WorkItems Completed" check
already implies the dependency graph is satisfied, because
`ITaskDependencyResolver`/the scheduler's own dispatch ordering (both
unmodified) never let a dependent WorkItem reach `Completed` before its
dependency did. `CompletedDependency_AllowsProjectCompletionEvaluation` and
`UncompletedDependency_PreventsProjectCompletionEvaluation` both assert this
directly against a real `WorkItemDependency` row persisted via the existing
`IWorkItemRepository.AddDependencyAsync`.

## 13. Concurrency / Idempotency

- All Project mutations go through `Project.TransitionTo` +
  `IProjectRepository.SaveChangesAsync`, guarded by the existing
  `Version`/`ConcurrencyConflictException` mechanism — no new locking, no
  distributed lock, no static ownership map.
- A lost race (`ConcurrencyConflictException`) inside
  `ProjectCompletionGate.TryTransitionAsync` is caught and treated as a safe
  no-op (`ProjectCompletionGateAction.None`), logged as
  `ProjectCompletionGateLostRace`, never retried/overwritten.
- `ConcurrentProjectGateEvaluation_IsConcurrencySafe` proves this directly:
  two independent `DbContext`s load the same `Implementation` Project and
  evaluate concurrently via `Task.WhenAll`; exactly one advances to `Testing`,
  the other safely no-ops.
- `RepeatedProjectGateEvaluation_IsIdempotent` and `AlreadyTerminalProject_IsNotOverwritten`
  prove repeated observations (including of an already-`Completed` Project)
  never re-transition or throw.
- `TaskDispatchScheduler.RunCycleAsync` isolates a failing gate evaluation
  (`try`/`catch (Exception ex) when (ex is not OperationCanceledException)`)
  so one Project's gate failure can never take down the rest of that cycle's
  WorkItem-dispatch result — the same failure-isolation discipline already
  used for `DispatchOneAsync`/`DispatchRepairAsync`.

## 14. Recovery Behavior

No new recovery framework was introduced — `WorkflowRecoveryHostedService`
is untouched. Because `ProjectCompletionGate.EvaluateAsync` is purely a
function of currently-persisted `Project.Status` + `WorkItem` rows (no
in-memory state of its own), it is inherently safe to re-run after a process
restart: `TaskSchedulerHostedService`'s existing periodic sweep will simply
re-evaluate any Project sitting in `Implementation`/`Testing`/`Reviewing`
again on the next tick. The one known gap (carried over from Phase 18.8,
unchanged by this phase) is that a WorkItem stuck mid-`Testing` after a crash
has no active recovery sweep — documented previously and not addressed here,
per the spec's "fix only if trivial" instruction (it is not trivial, since it
would touch `WorkflowRecoveryHostedService`, which this phase must leave
alone).

## 15. API Changes

None. The existing `GET /api/projects/{id}` already exposes `Project.Status`
(and now `CurrentError`) for observability, and the existing
`PATCH /api/projects/{id}/status` already supports the one explicit human
action this phase relies on (`HumanReview -> Completed`). No new endpoint was
added, per the spec's "do not create an endpoint solely for internal
automatic progression" instruction.

## 16. Tests Added

- **`ProjectCompletionGateTests.cs`** (26 tests) — gate-level coverage against
  real SQLite repositories, organized exactly per the spec's §21 categories:
  completion detection (8), dependency semantics (2), active AgentRuns (2),
  lifecycle (6), verification (6), idempotency/concurrency (3, incl. a true
  two-`DbContext` concurrent-race test), plus the empty-Project policy test.
  All 27 spec-named tests are covered (one theory collapses the 7
  "WithXWorkItem_IsNotEligible" cases into a single parameterized test rather
  than 7 near-identical methods).
- **`ProjectCompletionIntegrationTests.cs`** (1 test,
  `EndToEnd_AllWorkItemsComplete_ProjectTests_Reviews_AndReachesHumanReview`)
  — the full real chain: `TaskDispatchScheduler` dispatches 3 WorkItems ->
  real `AgentOrchestrator` (`FakeAgentRuntime` standing in for `IAgentRuntime`)
  -> real `AgentRunWorkItemSync` (fake `IVerificationGate`, Passed) completes
  all 3 -> `TaskDispatchScheduler`'s embedded `ProjectCompletionGate` walks
  `Implementation -> Testing -> Reviewing -> HumanReview` one scheduler cycle
  at a time, asserting the Project explicitly does **not** auto-complete, then
  proves `HumanReview -> Completed` via the existing
  `ProjectService.TransitionStatusAsync`.
- Mechanical compile-compatibility updates for the `TaskDispatchScheduler`
  constructor's new `IProjectCompletionGate` parameter and the
  `IVerificationGate`/`IAgentRunRepository` interfaces' new members, in:
  `TaskDispatchSchedulerTests.cs`, `RepairDispatchTests.cs`,
  `RepairVerificationLoopTests.cs`, `WorkItemExecutionIntegrationTests.cs`,
  `ProjectPlanSchedulerIntegrationTests.cs`, `ProjectPlannerServiceTests.cs`,
  `AgentRunWorkItemSyncTests.cs`, `AgentRunWorkerTests.cs` — no behavioral
  changes to any of these files' own test logic.
- Real Hermes E2E: **NOT RUN** (not enabled this session — see §19).

## 17. Exact Build/Test Results

- `dotnet build AI.Factory.slnx -c Debug` — **0 Warnung(en), 0 Fehler** (0
  warnings, 0 errors).
- `dotnet build AI.Factory.slnx -c Release` — **0 Warnung(en), 0 Fehler**.
- `dotnet test AI.Factory.slnx -c Debug --no-build` —
  `AI.Factory.Worker.Tests`: 85 passed, 0 failed;
  `AI.Factory.Api.Tests`: 283 passed, 0 failed;
  `AI.Factory.Application.Tests`: 747 passed, 0 failed.
  **Total: 1115 passed, 0 failed** (1088 before this phase + 27 new tests).
- Targeted run before the full suite:
  `--filter FullyQualifiedName~ProjectCompletionGateTests` → 26/26 passed;
  `--filter FullyQualifiedName~ProjectCompletionIntegrationTests` → 1/1 passed.

## 18. Migration Status

One additive migration: `20260911123059_AddProjectCurrentError` — adds a
single nullable `TEXT` column `CurrentError` to the `projects` table (mirrors
the existing, likewise-unconfigured `WorkItem.CurrentError`). Generated via
`dotnet ef migrations add AddProjectCurrentError --startup-project
../AI.Factory.Api --project .` (the `AI.Factory.Worker` startup project does
not reference `Microsoft.EntityFrameworkCore.Design`, so `AI.Factory.Api` was
used instead — same as it must have been for every prior migration in this
repo).

`dotnet ef migrations has-pending-model-changes --startup-project
../AI.Factory.Api --project .` → **"No changes have been made to the model
since the last migration."**, confirmed both immediately after generating the
migration and again after the full Debug/Release build+test pass above.

## 19. Real Hermes E2E Result

**NOT RUN.** `AI_FACTORY_HERMES_SMOKE_TEST` was not set this session, so
`RealHermesE2ETests` (unchanged by this phase) did not execute against a real
Hermes instance. Per the spec's own §23, this phase is fundamentally a
deterministic Project-domain gate and does not require real Hermes evidence
to reach a verified state — no result is fabricated here.

## 20. Known Limitations

- `Project.CurrentError` is a single field, overwritten on each new gate
  transition (no history) — matches the existing `WorkItem.CurrentError`
  convention exactly, deliberately not a log/history table.
- A genuine Project-level verification failure goes straight to `Failed`
  (never `Repairing`) — by design (§9 of the spec explicitly forbids a
  second, Project-wide repair loop). An operator must manually re-plan/re-run
  a failed Project; there is no automatic "fix the whole project" mechanism,
  only the existing WorkItem-level repair loop (Phase 18.9) for issues caught
  at the WorkItem level.
- The pre-existing `WorkItem`-`Testing`-state recovery gap (Phase 18.8) is
  unchanged — a crash mid-WorkItem-verification still has no dedicated
  recovery sweep. Not addressed here, per instruction to only fix it "if
  trivial" (it is not).
- `ProjectTransitions` still legally allows a manual
  `PATCH /api/projects/{id}/status` call to jump `Reviewing -> Completed`
  directly, bypassing `HumanReview` — this is pre-existing behavior of the
  manual override endpoint, not something this phase's automatic gate ever
  does itself, and changing the manual endpoint's authorization semantics
  was out of scope for this phase.
- `IAgentRunRepository.HasActiveRunsForProjectAsync` is a simple `AnyAsync` —
  for a Project with a very large historical `AgentRun` count this is still
  O(1) at the database/index level (no in-memory pagination), so no
  additional bound was needed.

## 21. Remaining Risks

- The two parallel transition systems described in §2
  (`ProjectTransitions`/`Project.TransitionTo` vs.
  `WorkflowTransitionValidator`/`IWorkflowEngine`/`WorkflowStateStore`) still
  both exist and both can mutate the same `Project.Status` column through
  different code paths (`ProjectService` vs. `ApprovalService`). This phase
  did not touch or resolve that pre-existing duplication — it only builds
  entirely on the `ProjectTransitions` side, consistent with every other
  Phase 18.3+ component. A future phase should decide whether the legacy
  `IWorkflowEngine` path should be retired or reconciled.
- No `AgentDefinition` seeding mechanism exists anywhere in this repo (a
  known, pre-existing operational gap, unchanged by this phase) — an
  operator must register an `Implementation`-type (and, for repair, a
  `Repair`-type) `AgentDefinition` before the pipeline this gate sits inside
  can dispatch anything at all.

## 22. Final Decision

Project Completion & Review Gate: VERIFIED
Final Decision: GO
