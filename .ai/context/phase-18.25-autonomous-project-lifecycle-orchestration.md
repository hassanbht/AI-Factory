# Phase 18.25 — Autonomous Project Lifecycle Orchestration & Production Start

## 1. Repository findings (inspected, not assumed)

Every caller of `ProjectService.TransitionStatusAsync` and every write to `Project.Status`, classified:

| Site | Class | Notes |
|---|---|---|
| `ProjectService.InitializeSourceAsync` → `TransitionTo(Analyzing)` | Production workflow | The only thing that produces `Analyzing`. Effectively the "source decision made" edge. |
| `ProjectService.TransitionStatusAsync` | Manual API (`PATCH /api/projects/{id}/status`) | Generic operator transition; already refuses `Completed`. |
| `ProjectService.ApproveAsync` → `TransitionTo(Completed)` | Production workflow | The sole human-approval boundary. |
| `ProjectCompletionGate.TryTransitionAsync` | Production workflow | Owns `Implementation → Testing → Reviewing → HumanReview` (and fail-closed `Failed`/`Cancelled`). |
| ~30 call sites across `AI.Factory.Application.Tests` / `AI.Factory.Worker.Tests` | Test fixture | Includes direct `project.Status = …` seeding in unit tests. |

**The gap:** nothing in production owned `Analyzing → Specification → Architecture → Planning → (Planner) → Implementation`. Those transitions existed only in test code
(`LivePlannerE2ETests.MoveProjectToPlanningAsync`, `GoldenProjectLifecycleE2ETests`, `LiveProjectLifecycleE2ETests`), i.e. the operator was the lifecycle engine for that segment.

Also confirmed: `IWorkflowEngine` / `WorkflowEngine` / `WorkflowStateStore` / `WorkflowTransitionValidator` / `ApprovalService` are absent from the tree (removed in Phase 18.12) and were **not** recreated here.

## 2. Authoritative Project start

**The exact event:** `IProjectLifecycleOrchestrator.StartAsync(projectId, actor)` succeeding, which persists `Project.LifecycleStartedAtUtc`.

Prerequisites, all checked before the marker is written:

- Project exists.
- `Status == Analyzing` — i.e. `InitializeSourceAsync` already made and persisted a successful source decision. `Created` is rejected with a message telling the caller to initialize the source first; every other status is rejected naming the actual status.
- `Description` is non-empty (the Planner has to plan against something).
- `RootPath` is non-empty.
- The workspace provisions successfully through the existing idempotent `IProjectWorkspaceProvisioner` (failure → `IOException` → existing 503 mapping, same convention as `ProjectService.CreateAsync`).
- `LifecycleStartedAtUtc` is null, or the call is an idempotent no-op.

A Project row existing is explicitly **not** a start signal. Source initialization is not re-performed here (§10): it is already final at `Analyzing`.

### Why a new column rather than inferring from `Status`

`Status` alone cannot distinguish "the Factory owns this Project's progression" from "an operator is walking it through by hand". Without an explicit marker the sweep would hijack every historical/manually-driven Project (including the Projects the Phase 18.18/18.20 golden tests drive by hand). One nullable column, one migration: `20260918092530_AddProjectLifecycleStartedAt` — `projects.LifecycleStartedAtUtc TEXT NULL`.

## 3. Lifecycle ownership

```
POST /api/projects/{id}/start   →  IProjectLifecycleOrchestrator.StartAsync   (marks the Project lifecycle-owned)
TaskSchedulerHostedService tick →  IProjectLifecycleOrchestrator.AdvanceAsync (Analyzing … → Implementation)
                                →  ITaskDispatchScheduler.RunCycleAsync       (WorkItem dispatch)
                                     └─ IProjectCompletionGate                (Implementation → Testing → Reviewing → HumanReview)
AgentRunWorker                  →  AgentOrchestrator → Hermes; Planner materialization
POST /api/projects/{id}/approve →  ProjectService.ApproveAsync                (HumanReview → Completed)
```

| Question | Answer |
|---|---|
| Who owns Project transitions? | `ProjectLifecycleOrchestrator` up to `Implementation`; `ProjectCompletionGate` from `Implementation` to `HumanReview`; `ProjectService.ApproveAsync` for `Completed`. No overlap — the orchestrator returns `None` for every completion-gate stage. |
| Who starts Planning? | The orchestrator (`Architecture → Planning`). |
| Who starts Project-level AgentRuns? | The orchestrator, via the unchanged `IProjectPlanner.GenerateAsync`. |
| Who starts WorkItem execution? | `TaskDispatchScheduler` / `AgentRunWorker` (unchanged). |
| Who performs Project verification? | `ProjectCompletionGate` via `IVerificationGate` (unchanged). |
| Who requests HumanReview? | `ProjectCompletionGate` (unchanged). |
| Who performs Human approval? | `ProjectService.ApproveAsync` only. |

The orchestrator is not a workflow engine: no state store of its own, no second transition table, no retry loop. It delegates validation to `Project.TransitionTo` / `ProjectTransitions` and never assigns `Project.Status`.

## 4. Deterministic transition rules

`ProjectLifecycleOrchestrator.PrePlanningChain` is an explicit map, not a computed successor:

```
Analyzing     → Specification
Specification → Architecture
Architecture  → Planning
```

`Analyzing`/`Specification`/`Architecture` have no executing agent in the current Factory (no matching `AgentDefinition` exists), so they are walked as validated no-work transitions rather than inventing agents for them. Nothing is skipped: each tick performs at most one step, and each step goes through the domain transition table, so an invalid target throws `InvalidProjectTransitionException` and the lifecycle fails closed rather than forcing a state.

`Planning` is the only conditional stage:

| Observed state | Action |
|---|---|
| A persisted plan exists (`IProjectPlanService.GetPlanAsync`) | `Planning → Implementation` |
| No Planner AgentRun yet | `IProjectPlanner.GenerateAsync` (once), stay in `Planning` |
| Planner AgentRun non-terminal | Wait |
| Planner AgentRun terminal, `Succeeded`, no plan | `IProjectPlanner.MaterializeAsync` (idempotent) — resolves both the same-cycle race and the crash window |
| Materialization `Succeeded` | `Planning → Implementation` |
| Materialization `CandidateInvalid` / `PlanValidationFailed` | Fail closed → `Failed`, `CurrentError` set |
| Planner AgentRun `Failed` / `Cancelled` / `TimedOut` | Fail closed → `Failed`, `CurrentError` set |

The Planner is never retried by the orchestrator: `GetLatestRunAsync` makes "already dispatched" observable, so a failed Planner produces a terminal Project, not a loop.

## 5. Idempotency and concurrency

- `StartAsync` returns early on a non-null `LifecycleStartedAtUtc` — before any other check — so a repeat start is a pure no-op whatever stage the Project has reached.
- Two genuinely concurrent starts race on the existing `Project.Version` concurrency token. The loser catches `ConcurrencyConflictException`, reloads, and returns the winner's persisted state (same pattern as `ProjectService.ApproveAsync`). Exactly one authoritative start; one `LifecycleStartedAtUtc`.
- Every automatic transition uses the same lost-race-is-a-no-op pattern as `ProjectCompletionGate`.
- No distributed lock was introduced: the repository's optimistic concurrency was already sufficient, and `ProjectPlanService`'s hash-checked idempotency plus the `(ProjectId, PlanKey)` unique index remain the final guarantee against duplicate WorkItems.

## 6. Crash recovery

All lifecycle state is the Project row. A restarted Worker re-reads it on the next sweep and continues; nothing is held in memory. The one genuinely dangerous window — Planner AgentRun `Succeeded` but the Worker died before `AgentRunWorker` materialized the plan — is closed by the orchestrator re-driving the idempotent `MaterializeAsync` instead of dispatching a second Planner.

## 7. API

New: `POST /api/projects/{id}/start`, body optional `{ "actor": "..." }`.

- Accepts **no** target status — the caller says "start this project", never "set this project to Architecture".
- Idempotent; returns the persisted Project DTO, now including `LifecycleStartedAtUtc` and `CurrentError`.
- `PATCH /api/projects/{id}/status` keeps its existing meaning unchanged — no endpoint was given a second semantics.

## 8. Auditability

Written through the existing `IAuditLogger` (no new infrastructure, no new `AuditEventType` values):

| Event | `EventType` | `Outcome` | Actor |
|---|---|---|---|
| Lifecycle start | `WorkflowStarted` | current status | `Human`/`System` + supplied actor |
| Each automatic transition | `WorkflowStateChanged` | target status | `System` / `ProjectLifecycleOrchestrator` |
| Planner dispatch | `AgentStarted` | `Planning` | `System` / `ProjectLifecycleOrchestrator` |
| Lifecycle failure | `WorkflowFailed` | `Failed` | `System` / `ProjectLifecycleOrchestrator` |
| Human approval | `HumanApproval` | `Approved` | `Human` (pre-existing) |

`ProjectCompletionGate`'s own stages (`Testing`/`Reviewing`/`HumanReview`) remain observable through its existing structured logs only — it was deliberately not modified (spec §2). Reconstructing those from the audit table is a known gap, noted below.

## 9. Files

**Added**

- `apps/src/AI.Factory.Application/Abstractions/IProjectLifecycleOrchestrator.cs`
- `apps/src/AI.Factory.Application/Orchestration/ProjectLifecycleOrchestrator.cs`
- `apps/src/AI.Factory.Infrastructure/Persistence/Migrations/20260918092530_AddProjectLifecycleStartedAt.cs` (+ designer)
- `apps/tests/AI.Factory.Application.Tests/ProjectLifecycleOrchestratorTests.cs`
- `.ai/context/phase-18.25-autonomous-project-lifecycle-orchestration.md`

**Modified**

- `apps/src/AI.Factory.Domain/Entities/Project.cs` — `LifecycleStartedAtUtc`
- `apps/src/AI.Factory.Application/Planning/IProjectPlanner.cs` / `ProjectPlanner.cs` — `GetLatestRunAsync`
- `apps/src/AI.Factory.Worker/TaskSchedulerHostedService.cs` — lifecycle advance in the existing sweep
- `apps/src/AI.Factory.Worker/Program.cs`, `apps/src/AI.Factory.Api/Program.cs` — DI, start endpoint, DTO
- `apps/tests/AI.Factory.Worker.Tests/Fixtures/LiveWorkerHost.cs` — 2s scheduling interval for live tests
- `apps/tests/AI.Factory.Worker.Tests/LivePlannerE2ETests.cs` — autonomous-start golden run
- `apps/tests/AI.Factory.Worker.Tests/AgentRunWorkerTests.cs` — fake planner implements the new member

## 10. Live evidence (OBSERVED)

One uninterrupted live run, 2026-09-18, real Docker + real Hermes + OpenRouter
(`z-ai/glm-5.3-flash` planner/review, `qwen/qwen3-coder` implementation/repair/test),
10m 27s. Persisted audit trail for project `6504891d-3521-4167-bccd-61af1fe9c77e`:

| TimestampUtc | Event | Outcome | Actor |
|---|---|---|---|
| 11:07:03.905 | WorkflowStarted | Analyzing | Human/qa-lead@factory.local |
| 11:07:05.396 | WorkflowStateChanged | Specification | System/ProjectLifecycleOrchestrator |
| 11:07:07.382 | WorkflowStateChanged | Architecture | System/ProjectLifecycleOrchestrator |
| 11:07:09.388 | WorkflowStateChanged | Planning | System/ProjectLifecycleOrchestrator |
| 11:07:11.594 | AgentStarted | Planning | System/ProjectLifecycleOrchestrator |
| 11:08:13.393 | WorkflowStateChanged | Implementation | System/ProjectLifecycleOrchestrator |

Downstream, from the same run:

- Plan persisted from a real Planner AgentRun: 2 tasks, `PlanHash=4ABA7D13…`.
- `greeting.update` timeline `Running → Testing → Repairing → Running → Testing → Repairing → Running → Testing → Completed`, `RepairAttemptCount=2` — two real repairs through the ordinary pipeline.
- Dependency respected from persisted records: `docs.release-notes` first AgentRun started 11:15:27.386, its prerequisite completed 11:15:24.705.
- `ProjectVerificationCompleted Status=Passed DurationMs=7308`, then `Implementation → Testing → HumanReview`.
- Lifecycle stopped at HumanReview; `TransitionStatusAsync(…, Completed)` refused; `ApproveAsync("qa-lead@factory.local")` produced Completed.

The test issued no intermediate Project status transition — its only Project-level
lifecycle calls were create, initialize source, one `StartAsync`, and `ApproveAsync`.

An earlier attempt against the previously-qualified gateway failed closed at
`Planning → Failed` because every provider route was exhausted (403/402). That is the
§15 fail-closed path, observed live: no plan, no Implementation, persisted diagnostic.

## 11. Known gaps

- `ProjectCompletionGate` writes no audit entries; its stages are reconstructable from structured logs only. Left alone deliberately (spec §2 forbids redesigning it absent a proven defect).
- `IProjectPlanner.GetLatestRunAsync` scans the newest 50 AgentRuns for the Project. Only consulted while the Project is in `Planning`, where no other AgentRun type exists, so the window cannot hide a Planner run in practice.
- A misconfigured Factory (no enabled `Planner` AgentDefinition) makes `GenerateAsync` throw every tick. The sweep isolates and logs it rather than failing the Project permanently — a deployment misconfiguration should self-heal on registration, not brick the Project.
