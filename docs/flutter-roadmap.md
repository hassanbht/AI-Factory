# Flutter Client — Implementation Roadmap

Target backend: AI Factory `v1.0.0` · Application: `apps/ai_factory_client`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-architecture.md`](flutter-architecture.md),
[`flutter-ux-spec.md`](flutter-ux-spec.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-ui-patterns.md`](flutter-ui-patterns.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md).

Phase numbering follows the operator's revision of 2026-09-19: localization is
folded into the foundation phase rather than occupying one of its own, and live
updates move to 19.9.

| Phase | Scope | State |
|---|---|---|
| 19.0 | API contract, UX spec, architecture, API gaps, roadmap | ✅ complete |
| **19.1** | **Flutter foundation + easy_localization EN/DE + theme + routing + Dio + GetX + responsive shell** | ✅ complete |
| **19.2** | **API client + DTOs + domain models + repositories** | ✅ complete |
| **19.3** | **Dashboard + Projects + Create + Start** | ✅ complete |
| **19.4** | **Project Detail + Lifecycle** | ✅ complete |
| **19.5** | **WorkItems + DAG** | ✅ complete |
| **19.6** | **Agent Runs + Execution Details** | ✅ complete |
| **19.7** | **Human Review + Approval** | ✅ complete |
| **19.8** | **Operations + Diagnostics + Audit + operator access** | ✅ complete |
| **19.9** | **Live updates / controlled polling** | ✅ complete |
| **19.10** | **Integration + UI hardening** | ✅ complete |
| 19.11 | Windows / Web release | |

---

## 19.1 — Foundation and first-class localization ✅

Delivered: Flutter project (Windows, Web, Android, iOS foundations); layered
folder structure; GetX bindings and routing; Material 3 light/dark theme with the
status palette; `WindowSizeClass` breakpoints and the adaptive shell (rail at
tablet and desktop width, bottom bar at compact width); Dio `ApiClient` with
correlation-id and redacting-logging interceptors; the complete `AppError`
hierarchy and `ErrorMapper`; the sealed `ViewState<T>` and the shared
loading/empty/error views; backend enum mirrors with `unknown` fallbacks;
`easy_localization` with English and German, a tri-state language preference,
runtime switching and persistence; Settings screen; 68 tests.

Foundation seams later phases build on, rather than re-inventing:
`AppShell(builder:)`, `AsyncView`, `AppError`, `ViewState`, `EnumLabels`,
`Formatters`, `AppBindings`.

## 19.2 — API client and repositories ✅

Delivered: `ApiEndpoints`; DTOs for every endpoint in scope with
`json_serializable`; UTC and `TimeSpan` JSON converters; 16 domain models; pure
mappers; six repository interfaces with API-backed implementations;
`RequestCancellation`; GetX registration by interface; fixtures recorded from a
live `v1.0.0` API; 94 new tests including an opt-in live smoke test.
Full detail: [`flutter-api-client.md`](flutter-api-client.md).

Deferred deliberately rather than built speculatively: orchestration-run and
audit-log repositories, which belong to the phases that display them (19.6 and
19.8).

## 19.3 — Dashboard and Projects ✅

Delivered: the Dashboard's three separated bands (needs attention · project
state · platform) with client-side status aggregation and health, scheduler and
agent-run tiles; the Projects screen with client-side filtering, sorting and a
virtualized list; the two-step Create Project flow; the Start action; the
Project Detail placeholder on a real `/projects/:projectId` route; four screen
controllers; the reusable `StatusChip`, `SectionCard`, `MetricTile`,
`UnavailableNote`, `ActionErrorBanner` and `ResponsiveTileGrid`; `RelativeTime`;
132 new tests.
Full detail: [`flutter-ui-patterns.md`](flutter-ui-patterns.md).

Two deviations from the plan above, both deliberate:

* **Create is two steps, not three.** The wizard performs `create` and
  `source`; **Start** is a separate action on the Projects screen, because
  starting is a distinct lifecycle decision taken against a project that
  already exists, and Phase 19.3 specifies it as its own workstream. The
  mandatory order, the per-step retry and the idempotent `409` handling are all
  implemented as planned.
* **Agent-run counters are attempted, not assumed.** `GET /api/agents/metrics`
  is behind the operational key, so a `401` renders as an explicit "requires an
  operational key" note rather than as an error — the key input arrives in 19.8
  (`G-OPS-KEY-UX`).

Verified against a live `v1.0.0` backend in both languages: created a project,
initialized its source, started it, refreshed and saw the lifecycle status
change; error and empty states exercised with the API stopped and with an empty
database.

## 19.4 — Project Detail and lifecycle ✅

Delivered: the Project Detail screen in place of the 19.3 placeholder, on the
same `/projects/:projectId` route — header with identity and the project-level
action; the `ProjectLifecycleWidget` track built from
`AI.Factory.Domain.Entities.ProjectTransitions`; Overview; Source with
credential redaction; the recorded-error panel; an explicit not-found state;
Start, Refresh and Back; EN/DE across all of it; compact / medium / expanded
layouts; `ProjectLifecycle` and `ProjectSourceView` as pure, unit-tested
presentation projections; the reusable `ProjectHeaderPanel`,
`RecordedErrorPanel`, `NotFoundView`, `Fact`/`FactList` and `TimestampText`;
74 new tests plus a live-API and an offline integration scenario.
Full detail: [`flutter-ui-patterns.md`](flutter-ui-patterns.md) §15–§17.

Three deviations from the plan above, all deliberate:

* **No tabs.** The tab bar was to carry WorkItems, AgentRuns, Acceptance,
  Artifacts and Review — every one of which is 19.5 or later. A tab strip of
  five empty tabs is a worse screen than four full sections, so the shell that
  hosts them is built when there is something to host.
* **No stage is ever marked "completed", and an interrupted project marks no
  stage at all.** `GET /api/projects/{id}` returns one status and no history,
  and `Paused`, `Failed` and `Cancelled` *overwrite* that status on the
  backend — so the stage at which a project stopped is not recoverable from the
  API. The lifecycle therefore places stages *relative to* the reported one and
  says so, instead of implying the history in §7 of the UX spec. This extends
  `G-STAGE-TIMESTAMPS`.
* **Only Start, Refresh and Back.** Pause, Resume, Cancel and Approve are all
  19.7 work, and Resume additionally has no determinable target: `PATCH /status`
  accepts a resume transition, but the API does not say which stage the project
  was paused in, so the client cannot name one without guessing.

**Done for:** a newly created, running, paused, repairing, failed, human-review,
completed and cancelled project, each with a widget test; and end to end against
a live `v1.0.0` backend in both languages.

## 19.5 — WorkItems and the dependency tree ✅

**Depends on:** 19.4.

Delivered: a `WorkItems` + `Dependencies` section on Project Detail, built on
the `getPlan`/`listByProject`/`getReadiness` repository methods Phase 19.2
already shipped — no new repository surface was needed. `WorkItemsController`
reads `GET .../plan` first and falls back to `GET .../tasks` as a flat list
on a 404 (no plan yet), exactly as `docs/flutter-ux-spec.md` §8 specifies;
selecting a row fetches that item's live readiness lazily, one call, only for
the selection. `validateDependencyGraph` (a pure function over `ProjectPlan`)
reports — never silently drops — a self-dependency, a duplicate dependency, a
duplicate plan key or work item id, an unknown dependency target and a
dependency cycle; `WorkItemDependencyTree` renders the plan as the row tree
the UX spec calls for (not a graph canvas), guards its own recursion against
cycles with an ancestor set, marks a node with more than one parent as
"shared", and — because a node whose only edges are invalid is not a `.roots`
member under `ProjectPlan`'s own definition — computes its own effective-roots
set so a malformed node still renders instead of silently vanishing. New
reusable widgets: `WorkItemTile`, `WorkItemDetailPanel`,
`WorkItemDependencyTree`, plus `StatusChip.workItem`/`.readiness` and three
new `EnumLabels` methods (`workItemType`, `workItemPriority`, `taskReadiness`).
29 new deterministic tests (13 graph-projection, 11 controller, 5 widget) on
top of the 390 the suite now carries end to end; `dart format`, `flutter
analyze`, Windows and Web release builds all pass.

Two deviations from the plan above, both deliberate:

* **No separate WorkItem Detail route.** Selection is inline — the same
  `WorkItemDetailPanel` is shown next to the list at medium/expanded width and
  below it at compact width, driven by `WorkItemsController.selectedWorkItemId`
  rather than a pushed `/projects/:id/workitems/:id` route. One code path for
  every breakpoint, and nothing in the spec required deep-linking to a single
  work item yet; the route shape stays reserved in `app_routes.dart`'s comment
  for whichever later phase needs it (AgentRun detail is the more likely
  reason to add it).
* **No `DependencyLegend` widget.** The tree is self-describing per row
  (status chip, "shared" icon, cycle marker with tooltip) rather than needing
  a separate key, so a fourth reusable component was not justified.

**Done for:** an empty project (no work items), a project with work items but
no accepted plan (flat list, explicit "no dependency plan" note), and a
multi-level plan with a shared dependency, each covered by widget tests in
both languages; the graph projection additionally covered against every
malformed input Workstream Y lists. **Not done against a live API**: the
current `WorkItemRepository`/`ProjectRepository` surface has no client-facing
way to create a task or submit a plan (that was never in this phase's scope —
the graph is read-only, per Workstream AE/K), so a live integration run can
only prove the empty-state path; `integration_test/work_items_flow_test.dart`
covers exactly that and documents why in its own docstring.

## 19.6 — Agent runs and execution details ✅

**Depends on:** 19.5.

Delivered: an `AgentRuns` section on Project Detail, built entirely on the
`AgentRunRepository.list`/`.listDefinitions` repository surface Phase 19.2
already shipped — no new repository method and no backend change was needed,
because `GET /api/agents/runs?projectId=` already supports the exact query
this phase needed. `AgentRunsController` reads one page (bounded at 100,
matching the backend's own cap) joined with the agent-definition list for the
name/type `AgentRunDto` does not itself carry; a status filter is a real
server-side query parameter and re-fetches, while a work-item filter (reached
from a WorkItem's new "View agent runs" action, and symmetrically from an
AgentRun's "View work item" action) narrows the already-loaded page
client-side, because the API has no `workItemId` filter (gap
`G-WORKITEM-EVENTS`, unchanged). Selection is inline against the already
loaded page — the list endpoint already returns each run's full
`structuredResult`, so there is no separate detail fetch and no per-row
over-fetch. New reusable widgets: `AgentRunTile`, `AgentRunDetailPanel` (agent
type/name, project, work item, status, attempt, timestamps, duration, failure
category, correlation id with a copy action, and the structured result's
summary/output/data/warnings/errors/artifacts/metadata, each shown only when
present), plus `StatusChip.agentRun` and `EnumLabels.structuredResultStatus`.
19 new deterministic tests (12 controller, 7 widget) on top of the 390 the
suite already carried; `dart format`, `flutter analyze`, Windows and Web
release builds all pass.

Two deviations from the plan above, both deliberate and both re-verified
against the live `v1.0.0` API contract before being written down rather than
assumed from the Phase 19.0 gap inventory:

* **No Orchestration Run Detail screen (Trace/Attempts/Log).** `G-RUN-LIST`
  is unchanged: `GET /api/orchestration-runs/{id}` exists, but nothing on
  `AgentRunDto` or `WorkItemDto` names an orchestration-run id, and the only
  other source, `ProjectDiagnosticSummary.latestRunId`, requires the
  operational key this client does not yet collect (Phase 19.8). A read-only
  AgentRuns screen therefore has no lineage path to reach that screen from,
  so building it now would be UI with nothing to link to it.
* **No cancellation action.** `POST /api/agents/runs/{id}/cancel` and
  `POST /api/orchestration-runs/{id}/cancel` both exist and are real, but no
  Workstream in this phase's brief specifies a cancel button, a confirmation
  flow, or a test for one — adding one would be scope invented past what was
  asked. `AgentRunRepository.cancelRun` remains available in the repository
  interface (Phase 19.2) for whichever phase does specify it.

**Done for:** an empty project (no agent runs), a project with succeeded and
failed runs including a structured result with warnings/errors/artifacts, and
the status/work-item filter interactions, each covered by widget tests in
both languages. **Not done against a live API**: the current client has no
action that creates an AgentRun (`POST /api/agents/runs` needs an
`agentDefinitionId` this screen never collects), and a real Worker execution
needs a working model provider — the pre-existing `ModelRoutingPreflight`
startup blocker documented in 19.5 was re-checked for this phase and is still
reproducible in this environment, unrelated to the Flutter client.
`integration_test/agent_runs_flow_test.dart` covers exactly the provable
empty-state path and documents both limitations in its own docstring.

## 19.7 — Human Review and approval ✅

Delivered: the Human Review section on Project Detail, self-managing off the
server-reported `Project.status` (`HumanReview` → full review + Approve;
`Completed` → compact note; anything else → absent) — the client never infers
eligibility from WorkItem or AgentRun state. Review Summary (work items,
project error, acceptance — `G-ACCEPTANCE`, still explicitly unavailable) and
Execution Evidence (agent runs, structured-result pointer, telemetry —
`G-RUN-TELEMETRY`, still unavailable) reuse the sibling `WorkItemsController`/
`AgentRunsController` data already loaded for the page (Workstream AH — no
duplicate fetch). Approve is gated client-side on `HumanReview` and
"not already approving", behind an explicit confirmation dialog naming the
project, and guarded against duplicate submission by the controller, not just
the disabled button. The only completion path is
`POST /api/projects/{id}/approve` → `ProjectDetailController.refreshData()`;
`Completed` is never set locally. `409`/`404` responses reload the
authoritative project and surface the conflict; other errors leave the
evidence on screen for a retry.

**Deviation from the original plan above:** no "Send back to implementation"
and no "Cancel project" actions. The current backend exposes no reject or
cancel endpoint (re-verified against `Program.cs` for this phase), and the
phase brief explicitly forbids adding one ("project rejection endpoint unless
already explicitly exposed"; "arbitrary project status mutation") — so Approve
is the only action offered, matching what the API actually supports.
**Not done against a live API:** an automated `HumanReview → Completed` run
through the real backend, for the same pre-existing `ModelRoutingPreflight`
blocker documented since 19.5 (a Worker/model-provider limitation, not a
Flutter defect) — the offline-mock path through `FakeApprovalRepository`
exercises the identical controller/widget code and is covered by widget tests
in both languages.

## 19.8 — Operations, diagnostics and audit ✅

Delivered: the Operations screen (API health, deployment, database, scheduler
activity, agent metrics, configuration), the audit log, and a per-project
diagnostics screen on its own route with its own controller. Six endpoints,
**six independent states**: one section answering `401` or `500` leaves the
other five exactly as they were, each retries on its own, and a refresh keeps
loaded values on screen (Workstream Z). Operator access is a real credential
flow — `flutter_secure_storage` behind `OperationalCredentialStore`, obscured
entry, never rendered back, never logged, never in a URL, with exactly one
retry after a `401` and then a stop.

Everything the API does not expose is stated rather than guessed: worker health
(`G-WORKER-HEALTH`) inside the scheduler section, per-check health detail
(`G-HEALTH-DETAIL`) inside API health, model routing (`G-ROUTING`) inside
configuration, system-wide stale runs (`G-STALE-GLOBAL`) inside diagnostics,
acceptance (`G-ACCEPTANCE`) inside diagnostics, and model/cost telemetry
(`G-RUN-TELEMETRY`) inside agent metrics.

**Scope notes.** Four operational endpoints exist and are deliberately not
consumed — `/api/operations/metrics`, `/api/operations/resilience`,
`/api/operations/active` and `/api/operations/{correlationId}`. None appears in
this phase's Definition of Done, and a panel per endpoint is not the same thing
as an operator screen. The correlation-id drill-down moves with them.

**Gap corrected with evidence:** `/api/agents/metrics` is **not** behind the
operational key, and `RequireAccessKey` defaults to `false` — see
[`flutter-api-gaps.md`](flutter-api-gaps.md) `G-OPS-KEY-UX`.

**Not done against a live API:** the native Windows integration binding on this
machine runs the Operations walk and the credential lifecycle, but does not
settle when a `401` is staged before the first frame, when a second screen is
opened mid-test, or when a second `testWidgets` re-enters the mounted app.
Those paths are covered in the widget harness instead, and the limitation is
recorded in `integration_test/operations_flow_test.dart` rather than hidden.

## 19.9 — Live updates and controlled polling ✅

Delivered: `PollingCoordinator`, `PollIntervals`, `PollOutcome`/`PollBackoff` and
`AppActivity` in `lib/core/polling/`; `LiveUpdatesScope` (route visibility) and
`LiveStatusIndicator` in `lib/presentation/widgets/live_updates.dart`. Polling is
integrated into the existing Dashboard, Projects, Project Detail (one coordinated
cycle for project, work items and agent runs), Diagnostics and Operations
controllers — no new controllers, no timer in any widget. Adaptive intervals,
terminal-state stop, bounded backoff, `401`/permanent-error stop, no-overlap
queueing, silent background refresh, immediate refresh after Start and approval,
and EN/DE `live.*` strings ([`flutter-architecture.md`](flutter-architecture.md)
§12, [`flutter-ui-patterns.md`](flutter-ui-patterns.md) §22).

**Verified with deterministic tests:** 82 new tests — coordinator (39, fake time),
controller integration (26), widgets and lifecycle (17). Suite 626 passed,
3 skipped, 0 failed.

**Verified against a real backend:** the API was started on a throwaway SQLite
file (path and 21/21 migrations proven from the API's log;
`C:\AI-Factory\data\factory.db` untouched). On native Windows, Project Detail
observed externally-driven `Implementation`, `HumanReview` and another
operator's approval to `Completed` in 4.5–5.2 s each without a manual refresh,
then stopped polling; the Dashboard counted an externally created project
(30 s idle interval, as designed).

**Not tested live:** work item and agent run changes (produced by the Worker and
Hermes, which were not run), `401` handling (no operational key configured),
network loss and recovery, and the app-lifecycle pause on a real window. All are
covered by the deterministic tests.

## 19.10 — Integration + UI hardening ✅

**Depends on:** 19.9.
**Delivers:** the full test matrix from
[`flutter-architecture.md`](flutter-architecture.md) §10, including goldens in
both locales; accessibility pass (keyboard operation, focus order, semantic
labels, contrast, 200 % text scaling, reduce motion); the contract-fixture
regeneration script in CI; a secret scan over `lib/`; performance work on large
lists; a soak test against the rate limits.
**Done when:** CI is green, the soak test shows no `429`, and no fixture drift is
outstanding.

Delivered (see [`flutter-testing.md`](flutter-testing.md)): 96 pixel goldens of the
ten real screens (EN/DE × compact/medium/expanded, light; six critical screens also
dark) with the clock, time zone, fonts, pixel ratio and data pinned; an accessibility
suite (semantic labels, keyboard-only critical flow, focus order, computed contrast
plus `textContrastGuideline`, 100–200 % text in both languages, reduced motion); a
stronger translation audit (orphans, plurals, placeholders, argument counts,
hard-coded literals); `tool/api_fixtures.dart` (capture → normalize → commit, and
`--check` for drift) with 17 fixtures, two of them new areas (audit log, approval);
`tool/secret_scan.dart` for values, not references; large-list, dependency-tree and
rebuild benchmarks; a six-hour deterministic polling soak with a model of the
backend limiter; navigation, state-restoration and error-matrix regressions; four new
real-backend integration tests; and two credential-free CI jobs.

Defects found and fixed while hardening: "1 attempts" and five other count strings
(now plurals); Create Project's action row overflowing at 200 % German; the
dependency tree expanding converging plans exponentially, recursing on deep chains
and truncating titles at phone width; the agent-run filter overflowing at 200 %;
Escape doing nothing in both dialogs; the Create Project dialog disposing its text
fields before its exit transition ended; neutral, success, warning and (light)
danger chips below 4.5:1; focus nearly invisible on buttons and tiles; the approval
consequence announced on a wrapper rather than the focused button; no reduced-motion
handling; a release build defaulting to the development API; a Web build that would
have persisted the operational key in `localStorage`; every poll rebuilding every row
even when nothing had changed; 21 orphan translation keys and one dead widget.

## 19.11 — Windows / Web release

**Depends on:** 19.10.
**Delivers:** Windows packaging and versioning aligned with the backend `VERSION`;
a Web build with the documented CORS origin requirement; release notes under
`docs/releases/`; an operator quick-start (base URL, operational key, language);
and the explicit statement that Web builds are not configured with an operational
key (`G-OPS-KEY-UX`).
**Done when:** a Windows build completes the full create → source → start →
observe → approve flow against a containerized `v1.0.0` backend, in English and
German.

---

## Dependency graph

    19.1 Foundation + EN/DE localization          [done]
      |
    19.2 API client + repositories
      |
    19.3 Dashboard + Projects
      |
    19.4 Project Detail + Lifecycle
      |
    19.5 WorkItems + DAG
      |
    19.6 Agent Runs
      |
    19.7 Human Review + Approval
      |
    19.8 Operations + Diagnostics
      |
    19.9 Live updates / polling
      |
    19.10 Integration + UI hardening
      |
    19.11 Windows / Web release

## Backend phases that would unblock deferred UI

| Client capability | Blocking gap | Earliest useful backend phase |
|---|---|---|
| Acceptance tab, acceptance evidence in review | `G-ACCEPTANCE` | before 19.7 |
| Correct audit severity filtering | `G-AUDIT-SEVERITY` | before 19.8 |
| Single-call DAG, attempt ceiling | `G-DEPS`, `G-MAXATTEMPTS` | before 19.5 (optional; a workaround exists) |
| Execution history per project | `G-RUN-LIST` | before 19.6 |
| Model/provider telemetry, fallback reason | `G-RUN-TELEMETRY`, `G-FALLBACK` | before 19.6 |
| Authoritative worker health, per-check health | `G-WORKER-HEALTH`, `G-HEALTH-DETAIL` | before 19.8 |
| Push updates replacing polling | `G-REALTIME` | any time from 19.9 |

None of these blocks the roadmap: every phase ships with the documented
workaround and an explicit "not exposed by the API" state.
