# Phase 18.6 — LLM Planner Agent & Candidate Plan Generation

## 1. Executive Summary

Phase 18.6 closes the loop `Project -> Planner Agent -> LLM candidate plan ->
StructuredAgentResult -> ProjectPlanService -> WorkItems -> Scheduler`. It adds
no new execution infrastructure: the Planner Agent runs as an ordinary,
Project-level `AgentRun` (`WorkItemId = null`) through the unmodified
`AgentRunWorker -> AgentOrchestrator -> IAgentRuntime` pipeline built in Phase
18.1.x, and its untrusted output is mapped and handed to the unmodified,
authoritative Phase 18.5 `ProjectPlanService`. The only new code is a thin
mapping/orchestration layer (`PlannerResultMapper`, `ProjectPlanner`) plus one
new endpoint (`POST /api/projects/{projectId}/plan/generate`) and one small,
additive `AgentRunWorker` branch. No migration.

## 2. Repository Discovery

Inspected before writing any code: `Project`/`ProjectStatus`/`ProjectTransitions`,
`WorkItem`/`WorkItemType`/`WorkItemStatus`/`WorkItemDependency`,
`IProjectPlanService`/`ProjectPlanService` (Phase 18.5, full read),
`TaskDependencyService`/`ITaskDependencyResolver`/`TaskReadinessService`/
`TaskDispatchScheduler` (Phase 18.2/18.3, unchanged), `AgentRun`/`AgentDefinition`
(Domain entities), `AgentRunWorker` (Worker), `AgentOrchestrator`/
`IAgentInstructionBuilder`/`AgentInstructionBuilder`/`AgentExecutionInstruction`/
`IStructuredAgentResultParser`/`StructuredAgentResultParser`/`StructuredAgentResult`
(Phase 18.1.5/18.1.8), `AgentRunWorkItemSync` (Phase 18.3 §10),
`IAgentRunRepository`/`AgentRunRepository` (correlation-id idempotency,
`TryClaimNextPendingAsync`, `GetPagedAsync`), `IAgentRunService`/`AgentRunService`/
`CreateAgentRunCommand` (the existing AgentRun creation use-case), the existing
Agent API surface in `Program.cs` (`/api/agents/definitions`, `/api/agents/runs`),
and both `Program.cs` composition roots (Api and Worker — discovered the Worker's
root did **not** register `IProjectPlanService` at all; Phase 18.5 only wired it
into the Api host). Also inspected the legacy `ITaskPlanner`/`ModelTaskPlanner`/
`TaskPlan`/`PlannedTask`/`TaskPlanValidator`/`OrchestrationRunExecutionEngine`
pipeline (already characterized in the Phase 18.5 report) to confirm it remains
untouched and structurally unrelated.

## 3. Legacy Planner vs New Planner Architecture

Re-confirmed the Phase 18.5 finding: `ModelTaskPlanner` (legacy) produces a
`TaskPlan`/`PlannedTask` list that `OrchestrationRunExecutionEngine` folds into
**one existing WorkItem's Description text** — it never persists a DAG, never
creates WorkItems, and is wired exclusively into the old
`AutonomousCodingOrchestrator`/`HermesExecution` pipeline. It is a prompt-
augmentation helper for a single-task execution flow, not a plan-generation
subsystem. Phase 18.6 built a structurally separate pipeline (`ProjectPlanner`,
`PlannerResultMapper`) that produces exactly the same `ProjectPlanRequest`
shape Phase 18.5's HTTP `POST /api/projects/{projectId}/plan` already accepts
from a human/script caller — the LLM Planner is simply a second, untrusted
*producer* of that same contract. Neither pipeline was merged or modified to
accommodate the other, per §31's explicit boundary.

## 4. Planner Agent Architecture

```text
Project (Planning status)
    -> IProjectPlanner.GenerateAsync
    -> AgentRun { ProjectId = Project.Id, WorkItemId = null, AgentDefinitionId = <Planner> }
    -> AgentRunWorker (existing poll loop, unmodified claim path)
    -> AgentOrchestrator (existing, unmodified)
    -> IAgentRuntime (HermesAgentRuntime in production, FakeAgentRuntime in tests)
    -> AgentRun reaches a terminal status, StructuredResult persisted (existing mechanism)
    -> AgentRunWorker's new branch -> IProjectPlanner.MaterializeAsync
    -> PlannerResultMapper.Map (StructuredAgentResult -> ProjectPlanRequest)
    -> IProjectPlanService.CreatePlanAsync (Phase 18.5, unmodified)
    -> WorkItems + WorkItemDependencies persisted, scheduler-eligible
```

A dedicated `AgentDefinition.AgentType = "Planner"` (constant:
`ProjectPlanner.PlannerAgentType`) distinguishes the Planner from any
WorkItem-execution agent type — deliberately not one of the `WorkItemType`
enum values, so it can never collide with a plan task's own `type` (e.g.
`WorkItemType.Planning`). No AgentDefinition catalog is auto-seeded; callers
register their own Planner `AgentDefinition` via the existing
`POST /api/agents/definitions`, exactly like every other agent type.

## 5. AgentRun Lifecycle

Standard: `Created -> Queued -> Running -> Succeeded/Failed/Cancelled/TimedOut`
— entirely governed by the existing `AgentRunTransitions`/`AgentOrchestrator`,
unmodified. `WorkItemId` stays `null` for the life of a Planner AgentRun, so
`AgentRunWorkItemSync` (which returns immediately when `WorkItemId` is null)
never fires for it — no code change was needed there. `AgentRunWorker` gained
exactly one new `else if` branch, parallel to its existing
`WorkItemId is not null -> AgentRunWorkItemSync` branch:

```csharp
else if (result.Executed && run.WorkItemId is null && run.ProjectId is not null)
{
    var planner = executionScope.ServiceProvider.GetRequiredService<IProjectPlanner>();
    await planner.MaterializeAsync(run.Id, CancellationToken.None);
}
```

`IProjectPlanner.MaterializeAsync` itself re-checks `AgentDefinition.AgentType`
and terminal status, so it is a safe no-op for any other Project-level,
WorkItem-less AgentRun a future phase might introduce — the Worker branch does
not need to know about "Planner" as a concept at all.

## 6. Planner Instruction Contract

`AgentOrchestrator.BuildExecutionSnapshot` (unmodified) currently builds every
AgentRun's `AgentExecutionInstruction` with `Instructions` and `Constraints`
left as empty arrays — the only per-run variable channel today is
`AgentRun.Input`, which becomes the instruction's `Objective`. Rather than
special-casing the orchestrator for a Planner role (explicitly out of scope —
"do not create a second prompt-building framework"), `ProjectPlanner.
BuildObjective` composes a single, rich Objective string containing: the
untrusted-output framing, `Project.Name`/`Project.Description`, the exact
`data.plan.tasks[]` JSON shape (§7), the full list of valid `WorkItemType`
names (`Enum.GetNames<WorkItemType>()`) and valid priorities (now-public
`ProjectPlanService.ValidPriorities`, reused rather than duplicated), and
explicit rules: stable dotted `planKey`s (never GUIDs), `dependsOn` must
reference real prerequisite `planKey`s, no implementation code, no prose
outside the structured JSON, no tool/file/shell/Git actions. This still goes
through `IAgentInstructionBuilder` unchanged — the generic "Output contract"
footer (top-level envelope shape) is appended by the existing builder exactly
as for every other AgentRun.

## 7. Structured Result Contract

No change to `StructuredAgentResult`. The candidate plan is expected under the
existing free-form `Data` string field as:

```json
{"plan":{"version":1,"summary":"...","tasks":[
  {"planKey":"...","title":"...","description":"...","type":"...","priority":"...","dependsOn":["..."]}
]}}
```

`PlannerResultMapper.CurrentPlanVersion = 1` is this payload's own version
tag, independent of (and nested one level below) `StructuredAgentResult.
CurrentVersion` — the top-level envelope's version.

## 8. Candidate Mapping

`IPlannerResultMapper`/`PlannerResultMapper`
(`apps/src/AI.Factory.Application/Planning/`) converts
`StructuredAgentResult -> ProjectPlanRequest` and nothing else: no
persistence, no cycle checking, no readiness computation, no AgentRun
creation, no scheduling — enforced simply by never being given a database
handle. It rejects (throwing `PlannerCandidatePlanException`): non-Succeeded
structured status, missing/empty `Data`, unparseable JSON, missing `plan`
object, unsupported `plan.version`, empty/missing `tasks`, a task missing
`planKey`/`title`, and an unparseable/undefined `type` string. It does **not**
validate priority values, dependency-target existence, self-dependency,
cycles, duplicate keys, or AgentDefinition compatibility — those remain
exclusively `ProjectPlanService`'s job (§9/§17), never duplicated.

## 9. Validation Boundary

```text
PlannerResultMapper   : structural/type validity of the untrusted JSON (can this become a PlannedWorkItemInput at all?)
ProjectPlanService    : semantic plan validity (duplicates, unknown deps, self-dep, cycles, AgentDefinition compatibility, project lifecycle)
```

A candidate that fails the mapper never reaches `ProjectPlanService` at all;
one that passes the mapper but fails `ProjectPlanService` never persists a
single WorkItem (its existing atomic-transaction persistence guarantee,
unchanged). Both are surfaced as distinct `PlannerMaterializationOutcome`
values (`CandidateInvalid` vs `PlanValidationFailed`) — never collapsed, and
both are distinct again from `RuntimeFailed` (the AgentRun itself did not
succeed) per §15.

## 10. Idempotency/Concurrency Strategy

`IProjectPlanner.GenerateAsync`:
1. Rejects if the Project already has a persisted plan
   (`ProjectPlanService.GetPlanAsync != null`) — `ConflictException`.
2. Reuses an already in-flight (non-terminal) Planner `AgentRun` for the same
   `(ProjectId, AgentDefinitionId)` instead of creating a second one — a
   bounded `GetPagedAsync` lookup (page size 20), not a distributed lock.

This is a best-effort de-dup, explicitly not a locking subsystem (§16 says to
avoid building one). The actual safety guarantee against a genuine concurrent
race is `ProjectPlanService.CreatePlanAsync`'s own hash-checked idempotency
and the `(ProjectId, PlanKey)` unique index from Phase 18.5: even if two
Planner AgentRuns were somehow created and both executed, only the first
`CreatePlanAsync` call can ever persist WorkItems — the second deterministically
gets an idempotent match (identical hash) or a `ConflictException`
(different hash), surfaced by `MaterializeAsync` as `Succeeded` or
`PlanValidationFailed` respectively, but **never** a duplicate WorkItem set.

## 11. Model Selection Decision

Added `PlannerOptions` (`apps/src/AI.Factory.Application/Planning/
PlannerOptions.cs`, bound from configuration section `"Planner"`) with a
single optional `string? Model` property — the smallest possible explicit
configuration, not a model-selection framework. `CreateAgentRunCommand`
(existing Phase 18.1.6 type) gained one optional trailing parameter,
`string? Model = null`, backward-compatible with every existing caller.
`ProjectPlanner.GenerateAsync` passes `_options.Model` straight into it, which
`AgentRunService.CreateAsync` now assigns to `AgentRun.Model` — the field
Phase 18.1.8 left unwired end-to-end. `null` (the default) preserves today's
behavior exactly: the runtime/provider picks its own default, same as every
other AgentRun.

## 12. API Changes

New: `POST /api/projects/{projectId}/plan/generate` — validates the project
id, calls `IProjectPlanner.GenerateAsync`, and returns `201 Created` at
`/api/agents/runs/{run.Id}` with the existing `ToAgentRunDto` shape (identical
convention to `POST /api/agents/runs`) — no new DTO shape was introduced.
Never executes Hermes inside the HTTP request; the AgentRun is left `Queued`
for the existing `AgentRunWorker`. Unchanged: `POST`/`GET
/api/projects/{projectId}/plan` (Phase 18.5) — the Planner's output reaches
that endpoint only after `ProjectPlanService.CreatePlanAsync` has run; a
`GET /api/projects/{projectId}/plan/generate` was not added — Planner
execution *progress* remains observable exclusively through the existing
`GET /api/agents/runs/{id}` (§26 — no raw LLM output exposed through the plan
API).

## 13. Test Coverage

- `PlannerResultMapperTests.cs` (Application.Tests, 11 tests) — mapping
  correctness plus every rejection category from §9 (missing data, missing
  `plan`, unsupported version, malformed task, invalid `type`, malformed
  `dependsOn`, empty task list, missing `planKey`, non-Succeeded input,
  default-priority behavior).
- `ProjectPlannerServiceTests.cs` (Application.Tests, 10 tests, 2 via
  `[Theory]`) — real `ProjectPlanner` + real `AgentOrchestrator` (with
  `FakeAgentRuntime`) + real SQLite-in-memory repositories: AgentRun shape
  (`ProjectId` set, `WorkItemId` null), structured-result persistence,
  full success materialization (WorkItems + dependencies persisted correctly,
  root scheduler-dispatched via a real `TaskDispatchScheduler.RunCycleAsync`
  cycle, dependent blocked), `PlanValidationFailed` with zero partial
  WorkItems, `CandidateInvalid` with zero partial WorkItems, `RuntimeFailed`
  for Failed/Timeout/Cancelled AgentRuns (zero WorkItems in every case),
  concurrent-generate reuse of the active run, and rejection of a second
  `GenerateAsync` once a plan already exists.
- `ProjectPlannerApiTests.cs` (Api.Tests, 6 tests) — `201` with the AgentRun
  shape, `409` for a non-Planning project, `404` for an unknown project,
  `400` when no Planner AgentDefinition is registered, idempotent reuse on a
  second call, and confirmation that `GET /plan` behavior is untouched.
- `AgentRunWorkerTests.cs` (Worker.Tests, +2 tests) — proves the Worker's new
  branch actually invokes `IProjectPlanner.MaterializeAsync` exactly once for
  a terminal, `WorkItemId == null` AgentRun, and never invokes it for a
  WorkItem-linked one (the pre-existing `AgentRunWorkItemSync` branch already
  covers that case, unchanged).
- `RealHermesE2ETests.cs` (Worker.Tests, +1 opt-in test) — see §15.

All new tests passed on first execution; no existing test was modified.

## 14. Fake Runtime Integration Result

**PASSED.** `ProjectPlannerServiceTests` proves, without any Hermes/network
dependency, the full deterministic chain: `Project -> ProjectPlanner ->
AgentOrchestrator(FakeAgentRuntime) -> AgentRun.StructuredResult ->
PlannerResultMapper -> ProjectPlanService -> WorkItems -> Dependencies ->
TaskDispatchScheduler dispatch`, for both the happy path and every documented
failure category (runtime failure, invalid candidate, invalid/rejected
candidate), always with zero partial WorkItems on any failure path.

## 15. Real Hermes E2E Result

`RealHermesE2ETests.Planner_RealAgentRun_ThroughFullFactoryPipeline_
MaterializesPersistedPlan` was added, gated behind
`AI_FACTORY_HERMES_SMOKE_TEST=true` (same opt-in convention as every other
test in that file) and routed through the same local OmniRoute gateway used
by the existing Hermes E2E suite. It seeds a Planner `AgentDefinition` plus
one `AgentDefinition` per `WorkItemType`, calls `IProjectPlanner.
GenerateAsync`, augments the production-generated Objective with a
`AI_FACTORY_PLANNER_E2E_OK` marker instruction (test-only, so the assertion
can honestly confirm fresh model output rather than a cached/fabricated
result), drives the real `AgentRunWorker` against real Hermes/OmniRoute, and
asserts the AgentRun reaches `Succeeded`, the marker appears in the
structured result, and `IProjectPlanService.GetPlanAsync` eventually returns
a non-empty, persisted plan.

This test was **not executed** in this session — `AI_FACTORY_HERMES_SMOKE_TEST`
was not set, so it returned immediately without contacting Hermes, exactly as
designed. Honestly reporting:

```text
Real Planner E2E: NOT RUN
```

## 16. Migration Status

**None required, and none created.** Repository inspection (§2) confirmed the
Planner needs only `AgentRun.ProjectId`, `AgentRun.WorkItemId` (nullable,
already supports null), `AgentRun.Model`, and `AgentRun.StructuredResult` —
all pre-existing columns from Phase 18.1.x. `dotnet ef migrations
has-pending-model-changes` was run after all code changes and reported no
pending model changes (§17).

## 17. Exact Verification Results

All commands run from `C:\AI-Factory\apps` (build/test) and
`C:\AI-Factory\apps\src\AI.Factory.Api` (EF), verbatim output:

- `dotnet build AI.Factory.slnx -c Debug` → **0 Warnung(en), 0 Fehler**
  (`Der Buildvorgang wurde erfolgreich ausgeführt.`)
- `dotnet build AI.Factory.slnx -c Release` → **0 Warnung(en), 0 Fehler**
- `dotnet test AI.Factory.slnx -c Debug --no-build`:
  - `AI.Factory.Worker.Tests.dll` → **Fehler: 0, erfolgreich: 84, gesamt: 84**
  - `AI.Factory.Application.Tests.dll` → **Fehler: 0, erfolgreich: 660, gesamt: 660**
  - `AI.Factory.Api.Tests.dll` → **Fehler: 0, erfolgreich: 283, gesamt: 283**
  - **Total: 1027/1027 passed** (997 pre-existing + 30 new this phase; zero
    regressions, zero flaky reruns needed)
- `dotnet ef migrations has-pending-model-changes --project
  ../AI.Factory.Infrastructure --startup-project .` →
  `No changes have been made to the model since the last migration.`
- Real Hermes Planner E2E: not run this session (§15) — reported honestly as
  `NOT RUN`, not claimed as passing.

## 18. Known Limitations

- The Planner's Objective text is the sole instruction channel available
  today, because `AgentOrchestrator.BuildExecutionSnapshot` still hard-codes
  empty `Instructions`/`Constraints` arrays for every AgentRun (a pre-existing
  Phase 18.1.8 limitation, not something this phase was asked to fix). A
  future phase giving roles a first-class `Instructions`/`Constraints` channel
  could move some of `BuildObjective`'s content there.
- `GenerateAsync`'s in-flight-run de-dup is a bounded (`PageSize: 20`) recent-
  run scan, not an index-backed "one active run per project" query — correct
  for the expected one-Planner-at-a-time usage pattern, but a project with
  many stale AgentRuns could in principle need a slightly larger page (this
  is a scan-size tuning knob, not a correctness gap: `CreatePlanAsync`'s own
  idempotency is the actual correctness guarantee, per §10).
- Re-planning an already-planned Project is intentionally unsupported
  (`GenerateAsync` and `CreatePlanAsync` both reject it) — inherited directly
  from Phase 18.5's own explicit scope boundary, not a new limitation.

## 19. Remaining Risks

- A real LLM may occasionally produce a `type`/`priority` combination that,
  while structurally valid to the mapper, has no matching `AgentDefinition`
  registered — this surfaces cleanly as `PlanValidationFailed` with zero
  partial WorkItems (proven in §14), never a silent partial plan, but an
  operator would need to register the missing `AgentDefinition` and re-run
  Planner generation (blocked today by the no-re-plan rule above, until a
  future phase revisits it).
- The one-`AgentDefinition`-per-`WorkItemType` seeding used by the real-Hermes
  E2E test is a test-only convenience; a production deployment must still
  ensure the AgentDefinitions it actually intends to support are registered
  before generating a plan.

## 20. Final Decision

**LLM Planner Agent: VERIFIED**

**Final Decision: GO**
