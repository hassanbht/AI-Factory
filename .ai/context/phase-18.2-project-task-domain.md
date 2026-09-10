# Phase 18.2 — Project & Task Domain Foundation

## Summary

Extends the pre-existing Project + WorkItem domain (discovered via inventory, not built
from scratch — see "Discovery" below) with explicit lifecycle state machines, task
dependency write-side validation, a deterministic readiness query, and a REST API
surface. Zero EF migrations required. No real-Hermes execution added or run — this
phase touches only CRUD/state-machine/graph logic around AgentRun, never its
execution path.

## Discovery

Before writing anything, an inventory pass found a full pre-existing domain already in
the codebase from an earlier phase:

- `Project` entity + `ProjectStatus` enum (Created → Analyzing → Specification →
  Architecture → Planning → Implementation → Testing → Repairing → Reviewing →
  HumanReview → Completed/Failed/Cancelled)
- `WorkItem` entity + `WorkItemDependency` join entity (self-referencing, same-project
  dependency edges)
- `TaskDependencyResolver` (`ITaskDependencyResolver`) — DFS-based cycle detection +
  per-item readiness classification, already returning `TaskReadinessStatus`
- `TaskScheduler` / `TaskStateManager` — pre-existing consumers of the resolver
- `AgentRun.ProjectId` / `AgentRun.WorkItemId` — pre-existing nullable scalar fields,
  already backward-compatible

This reframed the phase from "build the domain" to "add the missing pieces": explicit
transition validation, write-side dependency guards, a single-task readiness lookup,
and the REST surface — reusing every existing abstraction rather than duplicating it.

## Final models

### Project

`Project` (unchanged shape) + `ProjectStatus` enum, with one addition:
`ProjectStatus.Paused = 55` (string-converted in EF config — no migration). Paused sits
between Planning (40) and Implementation (50) numerically but is reachable from, and
returns to, every non-terminal status (see state machine below).

### Task (WorkItem)

`WorkItem` (unchanged shape), `WorkItemStatus` (unchanged: Draft, Ready, Running,
Testing, Repairing, Reviewing, Blocked, Completed, Failed, Cancelled — status stored as
`string`, not enum-typed, pre-existing). No new hardcoded per-role task types were
added — `WorkItem.Type` (`WorkItemType`) remains the only differentiator; a future
Planner/Coding/Review agent produces a `WorkItem` with a `Type`, not a new entity.

## Lifecycle state machines

Both machines follow the existing `AgentRunTransitions` precedent: a static
`Dictionary<TStatus, IReadOnlyCollection<TStatus>>` + `IsValid(from, to)` +
`IsTerminal(status)`, paired with `Invalid{X}TransitionException : InvalidOperationException`
and an entity `TransitionTo(next)` method that is the only sanctioned way to mutate
`Status`.

### ProjectTransitions (`ProjectTransitions.cs`)

- Linear happy path: Created → Analyzing → Specification → Architecture → Planning →
  Implementation → Testing → Reviewing → Completed, with Testing ⇄ Repairing and
  Reviewing → HumanReview → (Completed | back to Implementation) loops for
  repair/human-review cycles.
- Every non-terminal status can transition to `Paused`; `Paused` can resume to any of
  the non-terminal statuses or go to `Cancelled`.
- Every non-terminal status can transition to `Failed` or `Cancelled`.
- Terminal: `Completed`, `Failed`, `Cancelled` — no outbound transitions.

### WorkItemTransitions (`WorkItemTransitions.cs`)

- Draft → Ready → Running → (Testing | Blocked | Completed | Failed | Cancelled)
- Testing → (Repairing | Reviewing | Completed | Failed | Cancelled)
- Repairing → (Testing | Failed | Cancelled)
- Reviewing → (Completed | Repairing | Failed | Cancelled)
- Blocked → (Ready | Cancelled)
- Terminal: `Completed`, `Failed`, `Cancelled`.
- `TransitionTo` also sets `StartedAtUtc` on first entry to `Running` and
  `CompletedAtUtc` on entry to any terminal status — mirroring `AgentRun.TransitionTo`.

## Dependency rules (`TaskDependencyService`)

Write-side validation in front of the pre-existing `WorkItemDependency` table and
`ITaskDependencyResolver`:

1. **Self-dependency** — `workItemId == dependsOnWorkItemId` → `ValidationException` (400).
2. **Not found** — either task missing → `NotFoundException` (404).
3. **Cross-project** — tasks belong to different projects → `ValidationException` (400).
4. **Duplicate** — edge already exists → `ConflictException` (409).
5. **Circular dependency** — before persisting, the candidate edge is simulated
   in-memory against the full project's dependency graph (reusing
   `TaskDependencyResolver`'s existing DFS cycle detector, not a second algorithm) and
   removed again in a `finally` block regardless of outcome — the simulated edge lives
   only in a local, in-memory `List<WorkItemDependency>` and is never sent to
   `SaveChanges`. `CircularWorkItemDependencyException` is caught and rethrown as
   `ValidationException` (400).
6. **Removal** — `RemoveDependencyAsync` throws `NotFoundException` (404) if the edge
   does not exist; otherwise removes it and returns.

### Bug found and fixed during this phase

The simulation step above originally added the candidate `WorkItemDependency` directly
to the tracked EF collection (`mutableItem.Dependencies`) without removing it before
calling the repository's real insert. Because the simulation and the real insert share
one `DbContext` per service call, EF's change tracker picked up the simulated instance
on `SaveChanges` and collided with the newly-`Add`ed real instance — both carrying the
same `{WorkItemId, DependsOnWorkItemId}` key — throwing
`InvalidOperationException: cannot be tracked because another instance ... is already
being tracked`. Caught by `TaskDependencyServiceTests.AddDependencyAsync_Valid_Persists`
and two `TaskReadinessServiceTests` cases. Fixed by removing the simulated edge from
the tracked collection in a `finally` block immediately after the cycle check, before
the real `AddDependencyAsync` call. All previously-failing tests pass after the fix;
full regression re-run confirmed no other test relied on the old (broken) behavior.

## Readiness semantics (`TaskReadinessService`)

`GetReadinessAsync(taskId)` — deterministic, no LLM/Hermes call:

1. Load the task (`NotFoundException` if missing).
2. Load the full project's `WorkItem` graph.
3. Run the existing `ITaskDependencyResolver.Resolve(...)` over the whole graph (cycle
   detection + per-item classification already implemented there).
4. Return the single requested task's `TaskReadinessStatus`.

Classification (pre-existing resolver logic, unchanged):

- Own status `Running` / `Completed` / `Cancelled` / `Failed` → that status directly.
- No dependencies → `Ready`.
- Any dependency `Failed` → `BlockedByFailedDependency`.
- All dependencies `Completed` → `Ready`.
- Otherwise (any dependency pending/running/blocked) → `WaitingForDependency`.

This is a query only — the foundation for a future scheduler, not a scheduler. It
performs no side effects and triggers no execution.

## AgentRun relationships

`AgentRun.ProjectId` (`Guid?`) and `AgentRun.WorkItemId` (`Guid?`) already existed
before this phase and required no change to the entity. Confirmed via a new
integration test suite (`AgentRunProjectTaskLinkageTests`) that:

- An `AgentRun` with neither set still persists and reloads with both null (backward
  compatibility).
- An `AgentRun` linked to a `Project` persists and reloads with `ProjectId` set.
- An `AgentRun` linked to a `Project` + `WorkItem` persists and reloads with both set.
- An existing `StructuredResult` / `StructuredResultVersion` payload (Phase 18.1.8)
  is unaffected by either link.

`AgentRunStatus`, optimistic concurrency (`Version`), Worker claiming
(`TryClaimNextPendingAsync`), the Hermes runtime, and the structured-result contract
were not touched. The only AgentRun-adjacent API change was adding `run.WorkItemId`
to the existing `ToAgentRunDto` projection (already had `run.ProjectId`), keeping the
field nullable.

## Migrations

**None.** Confirmed via `dotnet ef migrations has-pending-model-changes` (twice — once
after the domain/API wiring, once again after the final bug fix) → "No changes have
been made to the model since the last migration." `ProjectStatus.Paused` is
string-converted (`HasConversion<string>()`); `WorkItem.Status` was already a plain
`string` CLR property. Neither required a schema change.

## API surface

All new endpoints registered with `.RequireRateLimiting("GeneralApi")`, returning
DTOs via local `ToProjectDto` / `ToWorkItemDto` mapping functions — no EF entities are
serialized directly. No endpoint synchronously invokes agent execution.

### Projects

- `POST /api/projects` — create (201 + Location)
- `GET /api/projects` — list
- `GET /api/projects/{id}` — get (404 if missing)
- `PATCH /api/projects/{id}/status` — transition (200; 400 invalid status name; 409
  invalid transition)

### Tasks

- `POST /api/projects/{projectId}/tasks` — create (201; 404 unknown project; 400
  invalid type)
- `GET /api/projects/{projectId}/tasks` — list by project
- `GET /api/tasks/{id}` — get (404 if missing)
- `PATCH /api/tasks/{id}/status` — transition (200; 400 invalid status name; 409
  invalid transition)
- `POST /api/tasks/{id}/dependencies` — add dependency (201; 400 self/cross-project/
  circular; 404 unknown task; 409 duplicate)
- `DELETE /api/tasks/{id}/dependencies/{dependsOnTaskId}` — remove (204; 404 if the
  edge doesn't exist)
- `GET /api/tasks/{id}/readiness` — deterministic readiness lookup (200; 404 unknown
  task)

`ApiExceptionMapper` was extended to map `InvalidProjectTransitionException` and
`InvalidWorkItemTransitionException` to 409, alongside the existing
`ConflictException` case.

## Tests

- `ProjectTransitionsTests` — `ProjectTransitions.IsValid`/`IsTerminal` theory
  coverage + `Project.TransitionTo` valid/invalid.
- `WorkItemTransitionsTests` — same pattern for `WorkItemTransitions`/`WorkItem.TransitionTo`,
  plus terminal-timestamp assertion.
- `ProjectServiceTests` — create/get/list/transition (valid, invalid, unknown-project).
- `WorkItemServiceTests` — create/get/list-by-project/transition (valid, invalid,
  unknown-project).
- `TaskDependencyServiceTests` — valid add, self-dependency, duplicate, cross-project,
  circular dependency, remove (existing succeeds, missing throws `NotFoundException`).
- `TaskReadinessServiceTests` — no dependencies, all dependencies completed, pending
  dependency, failed dependency, running dependency, own-status-running, unknown task.
- `AgentRunProjectTaskLinkageTests` — no-link/project-link/task-link persistence,
  structured-result integrity under a Project link.
- `ProjectsAndTasksApiTests` — full HTTP-level coverage of every new endpoint,
  including 400/404/409 paths (23 tests).

## Compatibility

- Existing `AgentRun` API/DTO shape unchanged except the additive, nullable
  `workItemId` field.
- No existing repository/service signature was removed or made incompatible — only
  additive interface members (`IProjectRepository.ListAsync`,
  `IWorkItemRepository.AddAsync`/`AddDependencyAsync`/`RemoveDependencyAsync`/
  `DependencyExistsAsync`), which required a mechanical fixture update in
  `WorkItemTaskSchedulerTests.FakeWorkItemRepository` (four `NotImplementedException`
  stubs) to keep that pre-existing test file compiling.
- No changes to `AgentRunStatus`, optimistic concurrency, Worker claiming, the Hermes
  runtime, or the structured-result contract.

## Known limitations

- Readiness is computed on demand only — there is no push/subscription mechanism and
  no scheduler that acts on it yet.
- Task creation does not automatically transition a Project's status, and vice versa —
  the two lifecycles are independent by design; wiring them together is future work.
- No pagination on `GET /api/projects` or `GET /api/projects/{projectId}/tasks` — fine
  at current scale, will need bounded paging (matching the existing `AgentRunPage`
  pattern) before either list grows large.
- Project/Task data grants no agent capabilities by itself — creating a Task does not
  grant filesystem/shell/Git/network/credential access; that remains gated entirely by
  the existing tool/runtime architecture.

## Next-phase recommendation

Build the actual scheduler that consumes `TaskReadinessService` to decide which
`Ready` task to dispatch next (claiming, ordering, concurrency limits) — this phase
deliberately stopped at the deterministic readiness *query*, not the scheduler itself,
per explicit scope.

## Verification

- `dotnet build apps/AI.Factory.slnx -c Debug` → 0 warnings, 0 errors.
- `dotnet build apps/AI.Factory.slnx -c Release` → 0 warnings, 0 errors.
- `dotnet ef migrations has-pending-model-changes` → no pending changes (run twice).
- `dotnet test apps/AI.Factory.slnx -c Debug --no-build` → **947/947 passed**
  (74 Worker.Tests + 604 Application.Tests + 269 Api.Tests), 0 failures, 0 skipped.
- No real-Hermes/OmniRoute execution was run this phase — none of the changes touched
  `AgentRun`/`AgentOrchestrator` execution behavior, only CRUD/state-machine/graph
  logic around it, per explicit phase-scope instruction.

**Project/Task Domain: VERIFIED**

**Final Decision: GO**
