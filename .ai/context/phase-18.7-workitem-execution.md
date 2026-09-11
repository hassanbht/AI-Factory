# Phase 18.7 — Deterministic WorkItem Execution Agent & Result Integration

## 1. Executive Summary

Phase 18.7 closes the loop `Ready WorkItem -> TaskDispatchScheduler -> AgentRun
-> Hermes -> StructuredAgentResult -> deterministic WorkItem result handling`.
Repository inspection found the WorkItem-execution AgentRun was already
created correctly by `TaskDispatchScheduler` (Phase 18.3) with proper
`ProjectId`/`WorkItemId` correlation — but `AgentRunWorkItemSync` (also Phase
18.3) synchronized purely off `AgentRunStatus`, ignoring `StructuredResult`
entirely: **every** `Succeeded` AgentRun became `WorkItem.Completed`,
regardless of what the agent actually reported. That was the exact gap this
phase closes. The fix is a small, additive extension of two existing files
(`AgentRunWorkItemSync`, `TaskDispatchScheduler`) plus one new mapper — no new
scheduler, no new AgentRun lifecycle, no new Hermes integration, no migration.

## 2. Repository Discovery

Traced, before any change: `TaskDispatchScheduler.DispatchOneAsync` (creates
the WorkItem-execution `AgentRun` with `ProjectId`/`WorkItemId` set, selects
`AgentDefinition` by `AgentType == WorkItemType.ToString()`, builds `Input`
via a bare `$"{Title}\n\n{Description}"` string — no `IAgentInstructionBuilder`
enrichment); `AgentRunWorkItemSync.SyncAsync`/`TryMapTarget` (the exact gap —
see §4); `AgentOrchestrator` (unchanged — already routes every AgentRun's
`Input` through `IAgentInstructionBuilder` as its `Objective`, exactly as
Phase 18.6 relied on for the Planner); `AgentRunWorker` (unchanged — already
calls `IAgentRunWorkItemSync.SyncAsync` for every `WorkItemId != null` run
after a terminal result); `StructuredAgentResult`/`StructuredAgentResultParser`
(unchanged, reused as-is); `WorkItemStatus`/`WorkItemTransitions` (full read —
the actual transition table, not assumed); `ITaskDependencyResolver.Classify`
(full read — **only** `WorkItemStatus.Completed` satisfies a dependency; this
directly shaped §8's policy decision, see §4/§8); Phase 18.5
`ProjectPlanService` and Phase 18.6 `ProjectPlanner`/`PlannerResultMapper`
(read for pattern precedent — Objective-enrichment and untrusted-result-mapper
conventions reused verbatim); `AgentRunWorkItemSyncTests.cs`,
`TaskDispatchSchedulerTests.cs`, `ProjectPlanSchedulerIntegrationTests.cs`
(existing coverage, all traced before editing); both `Program.cs` composition
roots; `RealHermesE2ETests.cs` (existing opt-in pattern, reused for §15).
Also found and resolved a real name collision: a legacy
`WorkItemExecutionOutcome` record already exists in
`AI.Factory.Application.Abstractions` (`IAutonomousCodingOrchestrator`'s
pre-18.1 return type) — the new mapper's outcome enum was named
`WorkItemExecutionResultOutcome` to avoid silently shadowing it.

## 3. Existing WorkItem Execution Path

```text
TaskDispatchScheduler.RunCycleAsync
    -> DispatchOneAsync: WorkItem.TransitionTo(Running), AgentRun{ProjectId,WorkItemId} created, Queued
    -> AgentRunWorker (unchanged): claims the AgentRun, calls AgentOrchestrator.ExecuteAsync
    -> AgentOrchestrator (unchanged): IAgentRuntime call, parses StructuredAgentResult, persists AgentRun terminal state
    -> AgentRunWorker (unchanged): reloads the completed AgentRun, calls IAgentRunWorkItemSync.SyncAsync
    -> AgentRunWorkItemSync (THE GAP): mapped purely from AgentRunStatus — Succeeded always -> WorkItem.Completed
```

Confirmed via `AgentRunWorkItemSyncTests.SyncAsync_TerminalAgentRun_TransitionsWorkItem`'s
original `[InlineData(AgentRunStatus.Succeeded, WorkItemStatus.Completed)]`
case, which constructed the `AgentRun` with **no** `StructuredResult` at all
and still expected `Completed` — the literal proof the gap existed as a
passing, blessed test.

## 4. Exact Gap Identified

Answering §4's questions directly from the code:

1. AgentRun creation: `TaskDispatchScheduler.DispatchOneAsync` — unchanged.
2. WorkItem objective -> `AgentRun.Input`: a bare title+description
   concatenation, never routed through `IAgentInstructionBuilder`'s
   `AgentExecutionInstruction` fields (those stay hardcoded empty by
   `AgentOrchestrator` for every role — see Phase 18.6's report §6 for the
   identical finding there). **Gap**: no execution-result contract, no
   dependency context, no explicit constraints told to the agent.
3. AgentDefinition selection: `AgentType == WorkItemType.ToString()` string
   match — already correct and reused unchanged; Planner AgentDefinitions
   (`AgentType == "Planner"`) can never match any `WorkItemType` name, so
   they were already structurally excluded from WorkItem dispatch (§9)
   without any new guard needed.
4/5. Success/failure: `AgentRunWorkItemSync.TryMapTarget` — a flat
   `AgentRunStatus -> WorkItemStatus` map, blind to `StructuredResult`.
6. `AgentRunWorkItemSync` used **only** terminal `AgentRunStatus` — it never
   read `StructuredAgentResult` at all. **This was the exact gap.**
7. A successful AgentRun could only ever produce `WorkItem.Completed` — never
   `Testing`/`Reviewing`/`Repairing` (those literal enum values exist but
   nothing ever selected them for a Succeeded AgentRun).
8. No execution-result contract existed yet — Phase 18.1.8's
   `StructuredAgentResult` envelope is generic (any role); nothing
   interpreted its `Data` payload for WorkItem-execution purposes.
9. Only `AgentRun.Output`/`StructuredResult` are persisted from the agent's
   response — sufficient to build on, no new column needed.
10. The smallest missing deterministic layer: a `StructuredAgentResult ->
    execution outcome` mapper, consumed by `AgentRunWorkItemSync` at the
    single point it already decides a Succeeded AgentRun's target status.

## 5. Architecture After Phase 18.7

```text
TaskDispatchScheduler (unchanged responsibilities, enriched Objective text)
        |
        v
     AgentRun (ProjectId + WorkItemId, unchanged correlation)
        |
        v
 AgentRunWorker (unchanged call site)
        |
        v
 AgentOrchestrator (unchanged)
        |
        v
    IAgentRuntime -> HermesAgentRuntime -> Hermes (unchanged)
        |
        v
 StructuredAgentResult (unchanged envelope)
        |
        v
 AgentRunWorkItemSync -> IWorkItemExecutionResultMapper (NEW — deterministic WorkItem result handling)
        |
        v
 WorkItem state transition (Completed / Blocked / Failed, via existing WorkItemTransitions)
```

No second scheduler, AgentRun lifecycle, Hermes integration, structured-result
envelope, dependency resolver, or state machine was introduced.

## 6. Execution Result Contract

Reused `StructuredAgentResult.Data` (unchanged type/field) to carry:

```json
{"execution":{"version":1,"status":"completed|repair_required","summary":"...","artifacts":[],"tests":[],"notes":[]}}
```

`WorkItemExecutionResultMapper.CurrentExecutionVersion = 1` is this payload's
own version — independent of both `StructuredAgentResult.CurrentVersion` and
the unrelated Planner `plan.version` from Phase 18.6, per §5's explicit
instruction. Only `status` and `summary` influence anything; `artifacts`/
`tests`/`notes` are parsed for shape-safety only and are **not** persisted or
used to drive any domain decision this phase (§19 "no unnecessary
migration") — no arbitrary LLM field can mutate domain state.

## 7. Result Mapping / Validation Boundary

`IWorkItemExecutionResultMapper`/`WorkItemExecutionResultMapper`
(`apps/src/AI.Factory.Application/Orchestration/`) does exactly one thing:
`StructuredAgentResult -> WorkItemExecutionResult` (an outcome +
optional summary), or throws `WorkItemExecutionResultInvalidException`. It
never touches a database, never creates an AgentRun, never schedules
anything, never resolves dependencies, never transitions a Project — enforced
simply by never being given any of those dependencies. Deliberately narrow
vocabulary: exactly two recognized `status` values (`completed`,
`repair_required`) map to `WorkItemExecutionResultOutcome.
{ExecutionSucceeded, ExecutionRequestedRepair}`; anything else (missing,
malformed JSON, wrong version, unrecognized status, wrong field types) is
rejected, never guessed. `AgentRunWorkItemSync` is the layer that decides what
each outcome — plus the two categories the mapper is never even called for
(`RuntimeFailed`, and "no valid structured result at all") — means for
`WorkItemStatus`.

## 8. WorkItem State Transition Policy

```text
AgentRun Failed / TimedOut / Cancelled            -> WorkItem Failed / Failed / Cancelled   (unchanged since Phase 18.3)
AgentRun Succeeded + no/invalid StructuredResult  -> WorkItem Failed   ("Execution result invalid", §12)
AgentRun Succeeded + ExecutionSucceeded            -> WorkItem Completed
AgentRun Succeeded + ExecutionRequestedRepair      -> WorkItem Blocked
```

Two decisions required inspecting the actual `WorkItemTransitions` table
rather than following §7's illustrative example verbatim, exactly as
instructed:

- **`ExecutionSucceeded -> Completed`, not `Testing`.** `Testing` was the
  first design tried, matching §7's own suggested phrasing ("a successful
  LLM process must not automatically mean successful software delivery").
  It was reverted after re-reading `ITaskDependencyResolver.Classify`
  (§2/§4): a dependency is satisfied **only** when its target's
  `WorkItemStatus` is exactly `Completed` — routing through `Testing` would
  permanently strand every dependent WorkItem, since nothing in this
  system's current scope (and "autonomous test-repair loops"/"code review
  agent" are explicit non-goals, §20) ever advances `Testing` onward.
  Modifying the resolver to also accept `Testing` would itself be a
  dependency-engine redesign — also an explicit non-goal. The "not
  automatically" principle is instead satisfied by what actually changed:
  `Completed` is now reachable only through a genuine, deterministically
  validated `"completed"` execution report, never merely because the
  AgentRun process exited successfully with no report at all.
- **`ExecutionRequestedRepair -> Blocked`, not `Repairing`.** §7's own
  example suggests `Repairing`, but `WorkItemTransitions.Allowed[Running]`
  does not include `Repairing` at all (only `Testing`, `Blocked`,
  `Completed`, `Failed`, `Cancelled`) — `Running -> Repairing` would throw
  `InvalidWorkItemTransitionException`. `Blocked` is reachable from `Running`
  and, unlike `Failed`, can return to `Ready` (`WorkItemTransitions.
  Allowed[Blocked] = [Ready, Cancelled]`) — the smallest state compatible
  with "the agent attempted it but could not finish; this is recoverable,
  not a terminal failure."

`WorkItem.CurrentError` is set for every non-`Completed` target (matching the
Phase 18.3 convention exactly), carrying either the AgentRun's own error
message (runtime failures), a description of why the structured result could
not be interpreted (invalid), or the agent's own reported summary (repair
requested).

## 9. Idempotency / Concurrency Behavior

Unchanged from Phase 18.3, still sufficient: `AgentRunWorkItemSync.SyncAsync`
re-checks the WorkItem's own current status (`current != WorkItemStatus.
Running -> skip`) before ever computing a target — a second observation of
the same terminal AgentRun finds the WorkItem already moved on and returns
without re-transitioning, never regressing a terminal WorkItem, never
resurrecting a cancelled/failed one, never overwriting a newer independent
result. The existing optimistic-concurrency `Version` token
(`ConcurrencyConflictException` -> "lost the race, do not retry") is reused
unchanged for the write itself. No distributed lock, no static in-memory
ownership was added — proven directly by
`WorkItemExecutionIntegrationTests.DuplicateTerminalResultProcessing_IsSafe`
(same terminal `AgentRun` object synced twice; asserts exactly one AgentRun
row, one WorkItem row, and the correct final status).

## 10. Tests Added

- `WorkItemExecutionResultMapperTests.cs` (13 tests) — every item in §13's
  unit-test list: valid `completed`/`repair_required`, unsupported version,
  missing `execution` object, missing `status`, malformed JSON, wrong JSON
  field types, missing `data` payload, invalid status value, empty
  `execution` object, non-Succeeded input, deterministic repeat mapping,
  case-insensitive/trimmed status parsing.
- `WorkItemExecutionIntegrationTests.cs` (5 tests) — real
  `TaskDispatchScheduler` + real `AgentOrchestrator`(`FakeAgentRuntime`) +
  real `AgentRunWorkItemSync` + real SQLite: dispatch links AgentRun and
  persists structured result then completes the WorkItem; an invalid
  (non-JSON) result produces no unsafe mutation (deterministically `Failed`,
  with `CurrentError` set); a runtime failure (`Failure` mode) produces
  deterministic `Failed`; duplicate terminal-result processing is safe (§9);
  a dependent WorkItem becomes dispatchable only after its dependency
  actually reaches `Completed` — the real `TaskDependencyResolver`, unmocked.
- `AgentRunWorkItemSyncTests.cs` (+6 net — 3 original Runtime-failure cases
  kept as a `[Theory]`, unchanged expectations; the original bare-Succeeded
  `[InlineData]` was split out because its own premise — "Succeeded with no
  StructuredResult means Completed" — is precisely the behavior this phase
  deliberately changes) — now covers `completed` -> `Completed`,
  `repair_required` -> `Blocked` (+ `CurrentError` carries the summary), no
  structured result -> `Failed`, and malformed execution data -> `Failed`.
- `TaskDispatchSchedulerTests.cs` / `ProjectPlanSchedulerIntegrationTests.cs`
  / `ProjectPlannerServiceTests.cs` — only their `CreateScheduler` helper's
  constructor call was updated for the new `IProjectRepository` parameter;
  no assertion was changed; all pre-existing scheduler behavior tests
  (selection, dependency, dispatch, idempotency, concurrency, race,
  failure-isolation) pass unmodified.
- `RealHermesE2ETests.cs` (+1 opt-in test) — see §12.

No Planner, ProjectPlanService, AgentRun-lifecycle, heartbeat, recovery, or
scheduler-selection test was duplicated or rewritten.

## 11. Exact Verification Results

All commands run from `C:\AI-Factory\apps` (build/test) and
`C:\AI-Factory\apps\src\AI.Factory.Api` (EF), verbatim:

- `dotnet build AI.Factory.slnx -c Debug` → **0 Warnung(en), 0 Fehler**
- `dotnet build AI.Factory.slnx -c Release` → **0 Warnung(en), 0 Fehler**
- `dotnet test AI.Factory.slnx -c Debug --no-build`:
  - `AI.Factory.Worker.Tests.dll` → **Fehler: 0, erfolgreich: 85, gesamt: 85**
  - `AI.Factory.Application.Tests.dll` → **Fehler: 0, erfolgreich: 681, gesamt: 681**
  - `AI.Factory.Api.Tests.dll` → **Fehler: 0, erfolgreich: 283, gesamt: 283**
  - **Total: 1049/1049 passed** (1027 pre-existing after Phase 18.6 + 22 new
    this phase; zero regressions)
- `dotnet ef migrations has-pending-model-changes --project
  ../AI.Factory.Infrastructure --startup-project .` →
  `No changes have been made to the model since the last migration.`

## 12. Real Hermes E2E Result

`RealHermesE2ETests.WorkItemExecution_RealAgentRun_ThroughFullFactoryPipeline_
UpdatesPersistedWorkItemState` was added, gated behind
`AI_FACTORY_HERMES_SMOKE_TEST=true` (same opt-in convention as every other
test in that file), routed through the same local OmniRoute gateway. It seeds
a Project + `Implementation` `AgentDefinition` + one `Ready` WorkItem, runs
the real `TaskDispatchScheduler` to dispatch it, augments the
production-generated Objective with an `AI_FACTORY_WORKITEM_EXECUTION_E2E_OK`
marker instruction (test-only, so the assertion can honestly confirm fresh
model output rather than a cached/fabricated result — same anti-fabrication
technique as Phase 18.6 §15), drives the real `AgentRunWorker`, and asserts
the AgentRun reaches `Succeeded` with the marker present in its structured
result, then polls the WorkItem until it leaves `Running` and asserts it
landed on one of the three deterministic outcomes (`Completed`/`Blocked`/
`Failed` — the real model's own `completed`-vs-`repair_required` choice is
not pinned, since that is genuinely model-dependent; that the mapping
pipeline reached *some* correct, non-`Running` state is what is proven).

This test was **not executed** in this session — `AI_FACTORY_HERMES_SMOKE_TEST`
was not set, so it returned immediately without contacting Hermes, exactly as
designed. Honestly reporting:

```text
Real WorkItem Execution E2E: NOT RUN
```

## 13. Migration Status

**VERIFIED — none required, none created.** Every piece of new state lives in
existing columns: `AgentRun.StructuredResult`/`Status`/`ProjectId`/
`WorkItemId` and `WorkItem.Status`/`CurrentError`/`Version` — all pre-existing.
`dotnet ef migrations has-pending-model-changes` (§11) confirms no pending
model changes after every code change in this phase.

## 14. Known Limitations

- **KNOWN LIMITATION**: `WorkItem.Blocked` (the `repair_required` target) has
  no automatic path back to `Ready` yet — `WorkItemTransitions.
  Allowed[Blocked] = [Ready, Cancelled]` exists structurally, but nothing in
  this phase's scope (or the current system) automatically re-queues a
  Blocked WorkItem; that requires a future repair-loop phase (explicitly a
  non-goal here, §20 "autonomous test-repair loops").
- **KNOWN LIMITATION**: `WorkItem.Testing`/`Reviewing` remain structurally
  present in `WorkItemStatus` but are not reached by this phase's policy at
  all (§8) — they await a future Test/Review WorkItemType execution flow,
  consistent with the CLAUDE.md roadmap's own separation of Implementation
  from Test/Repair/Review.
- **KNOWN LIMITATION**: `artifacts`/`tests`/`notes` in the execution contract
  are parsed for shape-safety but not persisted or interpreted this phase —
  intentional (§6/§19), not an oversight; a future phase can extend the
  mapper without touching its outcome contract if a genuine need for them
  appears.

## 15. Remaining Risks

- **RISK**: A real LLM's own judgment about `completed` vs `repair_required`
  is inherently the one part of this pipeline that cannot be made
  deterministic (§21 "The LLM may report facts about its execution; the
  Factory decides what those facts mean") — the Factory's mapping from that
  self-report to `WorkItemStatus` is fully deterministic and tested (§10),
  but a model that over-claims `"completed"` for genuinely incomplete work is
  a model-quality problem outside this phase's boundary, not a Factory
  determinism gap.
- **RISK**: `TaskDispatchScheduler`'s enriched Objective text (§8 in the
  prompt) now includes dependency titles read from the same already-loaded
  `items` list `RunCycleAsync` uses for readiness — zero additional I/O, but
  a project with very large dependency fan-in would see a proportionally
  longer Objective string; not a correctness risk, a prompt-size
  consideration for a future phase if it ever becomes material.

## 16. Final Decision

**WorkItem Execution Integration: VERIFIED**

**Final Decision: GO**
