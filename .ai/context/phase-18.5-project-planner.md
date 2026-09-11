# Phase 18.5 — Deterministic Project Planner & Execution Plan

## 1. Executive Summary

Builds the first real Project Planning Engine on top of the Project/
WorkItem/Dependency/Scheduler infrastructure verified through Phase 18.4:
`ProjectPlanService` validates, normalizes, and atomically persists an
untrusted candidate plan (stable-keyed tasks + dependencies) as WorkItems +
WorkItemDependencies, which the existing `TaskDispatchScheduler` (Phase
18.3) then consumes exactly as-is — no new readiness logic, no second
dependency-cycle algorithm, no new scheduler. Planning and task
decomposition only; no coding/test/review agent, no Hermes call, from this
phase.

## 2. Repository Discovery

Per §1, searched for existing planning abstractions before designing
anything — and found a **complete pre-existing legacy planning pipeline**:
`ITaskPlanner`/`ModelTaskPlanner` (LLM-backed, produces an in-memory
`TaskPlan`/`PlannedTask` graph via the legacy `IAgentExecutor`), reused by
`OrchestrationRunExecutionEngine`, and `TaskPlanValidator` (structural
validation: duplicate ids, missing dependency refs, cycles).

Critically, `OrchestrationRunExecutionEngine` does **not** persist a WorkItem
DAG from that plan — it generates a `TaskPlan` purely as extra prompt
context, appends it as text to **one single pre-existing WorkItem**'s
`Description`, and runs the legacy `AutonomousCodingOrchestrator` on that one
WorkItem. It never creates new WorkItems or `WorkItemDependency` rows. This
is a fundamentally different use case from what Phase 18.5 asks for (a
persisted, scheduler-consumable, dependency-graphed DAG for the *new*
AgentRun/`TaskDispatchScheduler` pipeline verified through Phase 18.4) — so
it was **not reused directly**. Also, `ModelTaskPlanner` immediately
collapses the model's string task ids into random `Guid`s during parsing,
which is the exact anti-pattern §7 warns against ("do not rely on random
GUIDs for deterministic comparison before persistence") — another reason
not to reuse `TaskPlan`/`PlannedTask` as the new plan contract.

What **was** reused: `ITaskDependencyResolver`/`TaskDependencyResolver`
(Phase 18.2's DFS cycle detector — used unmodified for the new Planner's
cycle check, §8's explicit "do not implement a second cycle-detection
algorithm"), `IDatabaseTransactionManager` (existing transaction
abstraction, §11's "do not create a custom transaction manager"),
`IWorkItemRepository`/`IProjectRepository`/`IAgentDefinitionRepository`
(unchanged), and the existing `Project`/`WorkItem`/`WorkItemDependency`
domain model (§3 — no second task model, no new `Plan` entity/table). Also
read: `AgentRun`, `AgentDefinition`, `AgentRunWorker`, `AgentOrchestrator`,
`AgentExecutionInstruction`, `StructuredAgentResult`, `AgentRunWorkItemSync`,
existing Project/Task APIs, and the Phase 18.2/18.3/18.4 reports.

## 3. Existing Planning/Task Infrastructure (left untouched)

- Legacy: `ITaskPlanner`/`ModelTaskPlanner`/`TaskPlanValidator`/
  `OrchestrationRunExecutionEngine`/`AutonomousCodingOrchestrator` — a
  complete, separate, LLM-driven single-WorkItem prompt-augmentation
  pipeline. Zero files in this pipeline were modified; its own tests
  (`ModelTaskPlannerTests`, `TaskPlanValidatorTests`,
  `OrchestrationRunExecutionEngineTests`) pass unchanged.
- New pipeline (Phase 18.2–18.4): `Project`/`WorkItem`/
  `WorkItemDependency`/`TaskDependencyService`/`ITaskDependencyResolver`/
  `TaskReadinessService`/`TaskDispatchScheduler`/`AgentRun`/
  `AgentRunWorker`/`AgentOrchestrator`/`AgentRunWorkItemSync`. This is
  what `ProjectPlanService` targets.

No persisted `Plan` entity was introduced (§3) — the "plan" is simply the
Project's own set of `PlanKey`-tagged WorkItems + their
`WorkItemDependency` rows; justified below (§4) is the one schema addition
that *was* genuinely necessary: `WorkItem.PlanKey`.

## 4. Planning Contract

`IProjectPlanService` (`Application/Abstractions/IProjectPlanService.cs`):

```
CreatePlanAsync(ProjectPlanRequest) -> ProjectPlanResult
GetPlanAsync(projectId) -> ProjectPlanResult?

PlannedWorkItemInput(PlanKey, Title, Description, Type, Priority, DependsOn[])
ProjectPlanRequest(ProjectId, Summary, Tasks[])
ProjectPlanTaskResult(PlanKey, WorkItemId, Title, Type, Priority, DependsOn[], Status, Readiness)
ProjectPlanResult(ProjectId, PlanHash, Tasks[])
```

Per §4, the planning input is deliberately **not** a rich
specification/architecture object — no such subsystem persists yet
(`Project` has only `Name`/`Description`/`RootPath`/`GitRepository`/
`GitBranch`; no Specification/Architecture entities exist). Rather than
building that whole subsystem (explicitly out of scope, §4), the input
contract is the smallest stable thing that lets the Factory do its actual
job here — validate, normalize, and persist a candidate task set. The
*source* of that candidate set (a human via the API, a fixture in a test,
or eventually an LLM-backed generator bridged through the real
`AgentRun`/Hermes pipeline) is deliberately decoupled and out of scope —
§5/§6 explicitly require treating any such candidate as untrusted input to
this same validate → normalize → persist pipeline regardless of its origin.
**Missing upstream input, documented**: there is no persisted
Specification/Architecture context a Planner could read from `Project`
today; a future phase adding that subsystem would only need to feed its
content into `ProjectPlanRequest`/an LLM-backed candidate-generator — this
service's contract does not need to change.

**Stable key**: `PlannedWorkItemInput.PlanKey` (§7) is the caller-supplied
deterministic string identity (e.g. `"feature.authentication"`). It is
persisted on `WorkItem.PlanKey` (new nullable column, justified in §9
below) — the *only* schema change this phase makes.

## 5. Deterministic Planning Rules

No LLM is invoked anywhere in this phase's implementation (§6: "if a
deterministic planner can be implemented without an LLM at this stage,
prefer that for the domain foundation" — taken literally). `CreatePlanAsync`
is a pure function of its input plus already-persisted DB state: task
ordering, dependency correctness, cycle resolution, readiness, duplicate
handling, and priority are all Factory-computed, never LLM-decided. The
candidate task list itself is untrusted input (§5) regardless of where a
future caller sources it from.

## 6. Validation Rules

Implemented in `ProjectPlanService.ValidateAndNormalize` +
`ValidateAgentDefinitionCompatibilityAsync` + `DetectCycles` (§8):

- Non-empty `PlanKey`, non-empty `Title`, `Type` must be a defined
  `WorkItemType`, `Priority` must be one of `Critical`/`High`/`Normal`/`Low`
  (case-insensitive) — the same priority vocabulary
  `TaskDispatchScheduler`'s ordering table already uses.
- Duplicate `PlanKey` (case-insensitive) rejected.
- A dependency referencing an unknown `PlanKey` rejected.
- Self-dependency (`A -> A`) rejected.
- Cycles rejected by building a **transient, never-persisted**
  `WorkItem`/`WorkItemDependency` graph (fresh `Guid` per `PlanKey`) and
  calling the existing `ITaskDependencyResolver.Resolve` on it — the exact
  same algorithm `TaskDependencyService` and `TaskDispatchScheduler` already
  rely on, with zero new cycle-detection code (§8's explicit requirement).
- **AgentDefinition compatibility** (§14): for every distinct `WorkItemType`
  in the plan, an enabled `AgentDefinition` with matching `AgentType` must
  already exist — checked via `IAgentDefinitionRepository.GetAllAsync()`
  once per request. Chosen strategy: **reject the whole plan early**
  (§14's "prefer early validation when the task is intended for automatic
  execution") rather than persisting a non-dispatchable task — the simpler,
  safer of the two options offered.
- Project must currently be in `ProjectStatus.Planning` (§13) — checked
  before any validation of the candidate tasks even runs.

## 7. Normalization

`ValidateAndNormalize`'s final step (§9): tasks reordered by `PlanKey`
(ordinal string sort), each task's `DependsOn` list deduplicated
(case-insensitive) and reordered the same way, `Priority` rewritten to its
canonical casing. No timestamp, random `Guid`, or process-specific state
participates in ordering or the plan hash. `ComputePlanHash` builds one
canonical string (`PlanKey|Title|Type|Priority|dep1,dep2,...` per task,
newline-joined, tasks in normalized order) and SHA-256-hashes it — proven
identical for the identical candidate plan across two *different* Projects
(`CreatePlanAsync_SameCandidatePlan_DifferentProjects_ProducesSameHash`),
which is the strongest form of "same input -> same normalized plan" I could
test without relying on any shared persisted state between the two calls.

## 8. Persistence/Transaction Strategy

`ProjectPlanService.PersistAsync`, wrapped in the existing
`IDatabaseTransactionManager.ExecuteInTransactionAsync` (§11 — no custom
transaction manager): creates every WorkItem first (each starting directly
at `WorkItemStatus.Ready` — see §10 rationale below), then every
`WorkItemDependency` edge, via the existing `IWorkItemRepository.AddAsync`/
`AddDependencyAsync` (not `TaskDependencyService.AddDependencyAsync` — see
below). All validation (§6) already ran to completion *before* this method
is even called, so there is nothing left to fail mid-persistence except a
genuine infrastructure error — in which case `IDatabaseTransactionManager`
rolls the whole transaction back, leaving zero partial WorkItems.
Empirically proven by
`CreatePlanAsync_ValidationFailure_LeavesNoPartialTasks` (a plan that fails
validation never reaches the transaction at all — validated by asserting
the project has no WorkItems afterward) and
`CreatePlanAsync_ValidPlan_PersistsAllWorkItemsAndDependencies`.

**Why raw repository calls, not `TaskDependencyService.AddDependencyAsync`
per edge**: `TaskDependencyService` re-validates self/duplicate/
cross-project/cycle *per edge* against the full project graph — which
`ProjectPlanService` has already validated once, for the *whole* candidate
graph, before persistence starts. Calling it per edge would be pure
redundant work (an O(edges × graph-size) re-check of already-proven
invariants), not "reuse for correctness" — the actual reuse (§8's
requirement) is the shared `ITaskDependencyResolver` call in §6 above.

## 9. Idempotency Strategy

Chosen strategy (§12): **return existing plan on an exact repeat; reject a
structurally different request**. No plan-versioning system was built
(§3/§22 explicitly discourage speculative abstractions). Concretely: before
persisting, `CreatePlanAsync` loads the Project's current WorkItems; if any
exist, it reconstructs their normalized plan (via `BuildResultFromPersisted`,
using each WorkItem's `PlanKey` and its dependencies' `PlanKey`s) and
recomputes that reconstruction's hash the same way. If it matches the new
request's hash, the *existing* `ProjectPlanResult` is returned — genuinely
idempotent, no DB write at all. If it differs, `ConflictException` (409).
Proven by `CreatePlanAsync_RepeatedIdenticalPlan_ReturnsExistingWithoutDuplicating`
(same `WorkItemId`s both times, WorkItem count stays 2) and
`CreatePlanAsync_DifferentPlanForAlreadyPlannedProject_Rejected`, plus the
matching HTTP-level idempotency test.

**Why `WorkItem.PlanKey` needed a migration** (the one schema change this
phase makes, weighed against §21's "prefer no migration if possible"):
without persisting the caller's stable key somewhere, none of the following
were achievable — (1) §7's requirement that "every generated task must have
a stable identity" surviving into the persisted record, (2) idempotency
reconstruction above (matching by `Title` text would be fragile and
semantically wrong — two different plans can legitimately share a title),
(3) §17's explicit requirement that `GET /api/projects/{id}/plan` expose
"task stable key" per task. A single nullable `string` column with a unique
`(ProjectId, PlanKey)` index (allowing unlimited `NULL`s, so every
WorkItem created outside planning is unaffected) is the minimal schema
addition that satisfies all three — not a new `Plan` entity/table, not
speculative. Migration `20260910110559_AddWorkItemPlanKey` — additive only
(`AddColumn` + `CreateIndex`), reviewed, no destructive operation.

## 10. Project Lifecycle Integration

`CreatePlanAsync` requires `Project.Status == Planning` (§13) — the caller
transitions the Project there first via the existing
`PATCH /api/projects/{id}/status` (Phase 18.2), exactly matching the
documented lifecycle `Architecture -> Planning -> Implementation`. Per §13's
"only auto-transition `Planning -> Implementation` if the existing
lifecycle and domain semantics clearly justify it; otherwise leave it
explicit" — **this phase leaves it explicit**: `CreatePlanAsync` does not
transition the Project to `Implementation` on success. The Planner's one
responsibility (§13) is generating and validating the WorkItem DAG; moving
the Project forward is a separate, existing, explicit operation the caller
already has (`PATCH /status`). Verified by
`CreatePlanAsync_ProjectNotInPlanningStatus_Rejected`
(`ConflictException`/409 for a Project still in `Created`).

**WorkItem initial status — Draft vs Ready (§10), and why `Ready` is the
only correct choice**: `ITaskDependencyResolver.Classify` (Phase 18.2,
unmodified) treats *any* non-terminal, non-`Running` WorkItem status
(including `Draft`) as a `TaskReadinessStatus.Ready` candidate once
dependencies are satisfied — but `TaskDispatchScheduler` (Phase 18.3, my
own earlier design decision) additionally requires the WorkItem's own
persisted `Status` to literally equal `WorkItemStatus.Ready` before it will
ever claim it, since only `Ready -> Running` is a valid
`WorkItemTransitions` edge. If planned tasks started at `Draft`, a
dependent task would never become dispatchable even after its dependencies
completed — nothing in the system auto-transitions `Draft -> Ready`. All
validation (§8) completes *before* any WorkItem is created, so there is no
"partially validated" state a `Ready` WorkItem could represent (§10's core
requirement) — persisting directly at `Ready` is therefore both safe and
the only choice that makes the persisted DAG actually schedulable
end-to-end.

## 11. Scheduler Integration

Verified with a real, non-mocked integration test
(`ProjectPlanSchedulerIntegrationTests.
PlannedDag_IsConsumedByScheduler_RootDispatched_DependentBlocked`), not
merely by both components compiling (§23's explicit requirement): a
two-task plan (`root`, `dependent -> root`) is created via
`ProjectPlanService`, then `TaskDispatchScheduler.RunCycleAsync` (Phase
18.3, unmodified) is run directly against the same database. First cycle:
only `root` is dispatched (an `AgentRun` is created for it, `root.Status`
becomes `Running`), `dependent` stays `Ready` with zero `AgentRun`s. After
manually completing `root` (mirroring what `AgentRunWorkItemSync` does in
production), a second cycle dispatches exactly `dependent`. The Planner
duplicates nothing owned by the scheduler (§18): no readiness
recomputation, no second dependency DFS, no scheduler ordering logic, no
`AgentRun`-claiming logic — all of it is the actual, unmodified Phase 18.3
component.

## 12. AgentDefinition Compatibility

Covered in §6 above — early rejection at plan-validation time
(`CreatePlanAsync_NoAgentDefinitionForType_Rejected`) rather than persisting
a non-dispatchable task. No new agent-type catalog was invented; the
Planner only checks against whatever `AgentDefinition`s the operator has
already registered (same convention `TaskDispatchScheduler` already uses:
`AgentDefinition.AgentType == WorkItem.Type.ToString()`).

## 13. AgentRun.Model Decision

Not applicable — this phase performs no LLM/`AgentRun` execution at all
(§6, §15). No model-selection field or configuration was added anywhere;
the Phase 18.1.8 `AgentRun.Model` end-to-end-wiring gap remains exactly as
it was, untouched, and out of scope here per §15's explicit instruction not
to build a broad model-selection subsystem.

## 14. API Changes

- `POST /api/projects/{projectId}/plan` — body `{ Summary?, Tasks: [{
  PlanKey, Title, Description?, Type, Priority?, DependsOn? }] }`. Validates
  the route id, parses each task's `Type` string to `WorkItemType` at the
  API boundary (same convention as every other task endpoint), calls
  `IProjectPlanService.CreatePlanAsync`, returns `200 OK` with the plan DTO
  (used for both a genuinely new plan and an idempotent repeat — no
  `Location` header implied, since the "was this new" distinction isn't
  meaningful to the caller). Never synchronously executes any WorkItem
  (§16) — no `POST /api/tasks/{id}/execute` or equivalent was added.
- `GET /api/projects/{projectId}/plan` — `200 OK` with the plan DTO (each
  task: `planKey`, `workItemId`, `title`, `type`, `priority`, `dependsOn`,
  `status`, `readiness`) or `404` if no plan exists yet (§17). No internal
  persistence details (e.g. raw `WorkItemDependency` rows) exposed.
- DI: `IProjectPlanService` registered scoped in `AI.Factory.Api`'s
  `Program.cs` only (Worker has no planning endpoint and does not need it).

## 15. Tests Added

- `AI.Factory.Application.Tests/ProjectPlanServiceTests.cs` — 15 tests:
  generation (valid input, deterministic hash across Projects), validation
  (missing key, duplicate key, missing dependency target, self-dependency,
  circular dependency, no AgentDefinition), normalization (task +
  dependency ordering, deduplication), persistence (all WorkItems +
  dependencies persisted, atomic — failed validation leaves zero tasks),
  idempotency (repeated identical plan returns existing without
  duplicating, a different plan against an already-planned Project is
  rejected), lifecycle (Project not in `Planning` rejected, no-plan-yet
  `GetPlanAsync` returns null).
- `AI.Factory.Application.Tests/ProjectPlanSchedulerIntegrationTests.cs` —
  1 test: the full Plan -> WorkItems -> Dependencies -> Readiness ->
  `TaskDispatchScheduler` -> `AgentRun` chain, described in §11.
- `AI.Factory.Api.Tests/ProjectPlanApiTests.cs` — 7 tests: valid plan
  creation (200, correct shape), Project-not-Planning (409), invalid
  `WorkItemType` string (400), circular dependency (400), repeated
  identical plan (200 both times, no duplication via the HTTP path), no
  plan yet (404), and the GET response shape (`planKey`/`type`/`priority`/
  `dependsOn`/`status`/`readiness` all present, per §17).

No Hermes was called in any test (§19) — every test uses the real
deterministic domain services against in-memory SQLite; no fake
planner/runtime was needed since this phase's implementation itself never
touches `IAgentRuntime`.

## 16. Exact Verification Results

```
dotnet build apps/AI.Factory.slnx -c Debug     → 0 Warnung(en), 0 Fehler
dotnet build apps/AI.Factory.slnx -c Release   → 0 Warnung(en), 0 Fehler
dotnet test  apps/AI.Factory.slnx -c Debug --no-build
    → AI.Factory.Worker.Tests:      81/81 passed
    → AI.Factory.Application.Tests: 639/639 passed (16 new: 15 ProjectPlanServiceTests + 1 ProjectPlanSchedulerIntegrationTests)
    → AI.Factory.Api.Tests:         277/277 passed (7 new: ProjectPlanApiTests)
    → TOTAL: 997/997 passed
dotnet ef migrations has-pending-model-changes (from apps/src/AI.Factory.Api)
    → checked BEFORE adding the migration: "Changes have been made to the model..."
    → checked AFTER adding the migration: "No changes have been made to the model since the last migration."
```

New Planner tests also re-run in isolation (`--filter`) before the full
regression, all green on first run.

## 17. Migration Status

One migration: `20260910110559_AddWorkItemPlanKey` — adds nullable
`work_items.PlanKey` (`TEXT`, max length 200) and a unique index on
`(ProjectId, PlanKey)`. Purely additive (`AddColumn`/`CreateIndex` only, no
`AlterColumn`/`DropColumn`/data migration), reviewed directly, confirmed
non-destructive. Justification for why this was genuinely necessary (not
speculative) is in §4/§9 above.

## 18. Real Hermes Result

Not run, and not applicable to what this phase implements: no LLM/`AgentRun`
execution path exists in `ProjectPlanService` at all (§6's deliberate
choice — deterministic, non-LLM planner for the domain foundation). §20
frames real-Hermes validation around "Planner AgentRun -> Hermes ->
structured planning result" — that specific execution path was not built
in this phase (would require a Planner `AgentDefinition` + structured
result parser bridge, explicitly beyond this phase's scope per §6/§22), so
there is nothing real-Hermes-shaped to validate yet. No real-Hermes
evidence is claimed.

## 19. Known Limitations

1. No Specification/Architecture persistence exists yet — the planning
   input is limited to whatever `ProjectPlanRequest.Tasks` the caller
   supplies directly; there is no automatic derivation from Project
   context. Documented in §4 as the deliberate, smallest-stable-abstraction
   choice.
2. No LLM-backed candidate-plan generator was wired into the real
   `AgentRun`/Hermes pipeline in this phase (§6/§20) — `ProjectPlanService`
   validates/normalizes/persists whatever candidate it's given, but nothing
   in this phase produces that candidate from an LLM through the
   established `AgentRun -> AgentRunWorker -> AgentOrchestrator ->
   IAgentRuntime` path. The contract (`ProjectPlanRequest`) is shaped so a
   future phase can add that generator without changing this service.
3. Idempotency detection is Project-scoped and all-or-nothing: a Project
   that already has *any* WorkItems with a `null` `PlanKey` (created via
   the plain task API, not through planning) will never match any candidate
   plan's hash and will always return `409 Conflict` on `CreatePlanAsync` —
   mixing plan-created and manually-created tasks in one Project is not
   supported by this phase.
4. Re-planning a Project whose plan needs to genuinely change (not just an
   identical repeat) is rejected outright rather than supported via a new
   plan version — a deliberate, simplest-safe choice per §12's menu of
   acceptable strategies, not a limitation of the underlying domain model
   (a future phase could add versioning without changing the persisted
   WorkItem shape).

## 20. Remaining Risks

- The `(ProjectId, PlanKey)` unique index is the only guard against two
  concurrent `CreatePlanAsync` calls for the same brand-new Project (both
  reading "no existing WorkItems" before either writes) — a genuine race
  would surface as a raw persistence exception out of
  `IDatabaseTransactionManager.ExecuteInTransactionAsync` (safely rolled
  back, never a silent duplicate) rather than a polished `409` with a
  friendly message. Not empirically exercised under concurrency in this
  phase's tests (unlike Phase 18.3/18.4's explicit race tests) — a
  reasonable follow-up if concurrent planning requests become a realistic
  scenario.
- `BuildResultFromPersisted` (used for both idempotency comparison and
  `GetPlanAsync`) silently drops a dependency edge whose target WorkItem
  has no `PlanKey` (e.g. a pre-existing, non-planned WorkItem was somehow
  wired as a dependency outside this service). This can't happen through
  `ProjectPlanService.CreatePlanAsync` itself (all dependency targets come
  from the same normalized candidate list), but isn't defended against for
  data manipulated through some other future path.

## 21. Final Decision

**Project Planner: VERIFIED**

**Final Decision: GO**
