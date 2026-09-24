# Flutter Client — UI Patterns

Phase 19.3 · Application: `apps/ai_factory_client` · Backend: AI Factory `v1.0.0`
Companion documents: [`flutter-ux-spec.md`](flutter-ux-spec.md),
[`flutter-architecture.md`](flutter-architecture.md),
[`flutter-api-client.md`](flutter-api-client.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md).

The rules every operator screen follows, and the widgets that enforce them.
Written down so the phases that add Project Detail, WorkItems, Agent Runs,
Human Review and Operations do not each invent their own answer.

One rule outranks the rest:

    The console never shows a number it cannot account for.

Everything below is a consequence of it.

---

## 1. Screen states

Every API-driven screen renders `ViewState<T>` through `AsyncView`, so all
seven cases are handled and a blank frame is impossible.

| State | What the operator sees |
|---|---|
| `Idle` / `Loading` | `LoadingView` — spinner and a sentence. Never an empty page |
| `Refreshing` | The previous content, plus a 2 px `LinearProgressIndicator` at the top. Content is never replaced by a spinner on a refresh |
| `Success` | The content |
| `Partial` | The content, plus a notice naming what could not be loaded |
| `Empty` | `EmptyView` — a real answer ("no projects yet"), distinct from a failure |
| `Failure` with `stale` | The stale content, so the last known state stays readable |
| `Failure` without `stale` | `ErrorView` — localized message, Retry, collapsed technical block |

Three distinctions are load-bearing, and each has its own state:

* **empty** ≠ **failed**. "There are no projects" and "we could not ask" are
  different facts.
* **empty** ≠ **filtered to nothing**. The Projects screen shows
  `projects.noMatches.*` with a Clear-filter action when rows exist but the
  filter hides them all. Telling an operator "no projects yet" while four
  exist is a lie the screen tells itself.
* **not available** ≠ **zero**. See §4.

### Composed screens

A screen that reads several endpoints separates the one it cannot live
without from the ones that merely enrich it. The Dashboard:

    GET /api/projects          -> required. Its failure is the screen's failure.
    /health/live + /ready      -> additive. Failure -> Partial, tile shows "Not available".
    GET /api/scheduler/status  -> additive.
    GET /api/agents/metrics    -> additive, and a 401 is not a failure at all (§4).

All four run in one `Future.wait`, each wrapped so a single failure cannot
cancel the others, and each result is unwrapped independently. Four sequential
round trips for one screen would be the wrong shape.

## 2. Cards and sections

| Widget | Use |
|---|---|
| `SectionCard` | A titled block. `subtitle` carries the provenance of what is inside — "Counted by this client from the full project list the API returns" belongs there, not in a tooltip |
| `MetricTile` | One counted figure. `onTap` should open the rows behind it; a number the operator cannot drill into is trivia |
| `ProjectListTile` | One project in a list (§6) |
| `StatusChip` | A backend state (§3) |
| `UnavailableNote` | Something deliberately not shown, and why (§4) |
| `ActionErrorBanner` | A failed *action*, above content that stays usable (§5) |
| `ResponsiveTileGrid` | Tiles at 1 / 2 / 4 columns by size class |
| `Fact` / `FactList` | A labelled value, and a wrap of them — one per row when compact (§15) |
| `TimestampText` | An API timestamp: relative on screen, absolute in the tooltip (§9) |
| `ProjectHeaderPanel` | Project identity plus the project-level action (§15) |
| `ProjectLifecycleWidget` | The backend lifecycle track with the reported state marked (§16) |
| `RecordedErrorPanel` | An error the **backend recorded on a resource** (§5) |
| `NotFoundView` | A `404` as its own state, with a route back rather than a retry (§5) |

## 3. Status chips

`StatusChip` is the single rendering of a backend state, and later phases use
it rather than adding another.

* Colour never carries meaning alone. Every chip is colour **plus** an icon
  **plus** a localized word, so it survives greyscale and colour blindness.
* `StatusTones` maps each backend enum to a tone in one place, so a project in
  `HumanReview` looks the same on the Dashboard, in the list and on Project
  Detail.
* `attention` is its own tone, distinct from both "running" and "done",
  because awaiting a human is the one state that needs a person.
* The label comes from `EnumLabels`, never from a literal in a widget. An
  unparsed value renders as `Unknown (RawValue)` — a state a newer backend
  introduced stays visible instead of turning into a blank.
* The label is never truncated. German status words are long, and a cut-off
  state is a misleading state.

Wire values (`HumanReview`, `TimedOut`) are identifiers. They are never
translated, never shown as-is where a label exists, and never sent back to the
API in translated form.

## 4. "Not available" is a first-class state

`UnavailableNote` says what is missing and why. It is the honest form of every
gap in [`flutter-api-gaps.md`](flutter-api-gaps.md) that reaches the UI, and
it exists so the two alternatives never happen: omitting the tile (the
operator cannot tell whether the value is zero or unknown) or filling it with
something plausible.

Three cases are distinguished, and they are not the same thing:

| Case | Rendering |
|---|---|
| The API does not expose it at all | `UnavailableNote` with the reason. Worker health (`G-WORKER-HEALTH`) is the example: scheduler activity is shown instead and labelled as *activity*, never as health |
| It needs an operational key the client has none for | `UnavailableNote` with `dashboard.platform.operationalKeyRequired`. A `401` from `/api/agents/metrics` is the normal state until Phase 19.8, so it is **not** an error and offers no Retry |
| The call failed | `Partial` plus a notice; the tile reads `common.notAvailable` |

A client-side aggregate is allowed where the endpoint returns the complete
collection, and must say so. The Dashboard's project counts are computed from
`GET /api/projects`, which is unpaged, so they are exact — but they are this
client's arithmetic, not a figure the backend reports, and the band's subtitle
states that.

## 5. Errors

Two kinds, and they never use the same surface.

**A screen that could not load** → `ErrorView`, in place of the content, with
Retry.

**An action that was rejected** → `ActionErrorBanner`, above content that
stays visible and usable. A rejected Start must not blank out the list the
operator is working in.

In both cases:

* The user-facing line is `error.messageKey.tr()` — always a translated
  sentence, never raw server text and never a stack trace.
* `technicalDetail` (ProblemDetails `detail`) and `correlationId` are for the
  operator: in the collapsed block of `ErrorView`, or the second line of the
  banner. The correlation id is the join key into `/api/operations/{id}`.
* Where the backend named the offending field ("Project has no initialized
  source"), that text is shown. It is the most useful sentence on the screen
  and this client could not have written it.

Two errors carry a reload, because they mean the client's copy is wrong:
`ConflictError` (409) and `NotFoundError` (404) on a mutation re-read the list
rather than leaving a row the operator cannot act on.

## 6. Adaptive layout

One widget, one data binding, arranged differently. Never two implementations
and never business logic behind a breakpoint.

`ProjectListTile` builds the same four pieces — identity, status, last update,
actions — as a `Row` of columns from tablet width up and as a stacked `Column`
below it. `_Identity` is shared by both arrangements so the truncation rules
cannot drift apart. `ProjectListHeader` renders the column captions only where
the rows are actually in columns.

| Size class | Dashboard | Projects |
|---|---|---|
| compact (< 600) | 1-column tiles, stacked | Cards, no column captions |
| medium (600–1023) | 2-column tiles | Columns, captions |
| expanded (≥ 1024) | 4-column tiles | Columns, captions |

Both lists use `ListView` builders, so an unpaged collection costs only the
visible rows.

German is the layout test, not English: its labels are consistently longer.
Every screen has a per-breakpoint widget test that switches to German and
asserts `tester.takeException()` is null, which is how a `RenderFlex`
overflow surfaces.

## 7. Action buttons and mutation safety

A mutation button is disabled while its own request is in flight. That is what
makes a double-click a no-op rather than two projects.

* **Per item, not per screen.** `ProjectsController` holds `RxSet<String>
  startingIds`, so starting one project never disables another project's
  button.
* **The guard is in the controller.** `startProject` drops a second call for
  the same id, and `submitDetails`/`submitSource` return early while
  `isSubmitting` is true. A widget that forgot to disable its button still
  cannot issue a duplicate request.
* **The label becomes a spinner**, so the disabled state is visible rather
  than mysterious.
* **Idempotency is the backend's, not a substitute for the guard.** `start`
  and `approve` are idempotent server-side, which makes a retry after an
  ambiguous timeout safe; it does not make an unguarded double-click correct.

The client never pre-judges the lifecycle. `ProjectListTile.canStart` checks
only `lifecycleStartedAtUtc`, which the API returned — "has this already been
started". Whether an unstarted project *may* start (source initialized,
description present) is the backend's decision, and it answers with a `409`
the screen shows. No status is ever written locally in anticipation: a row is
replaced only with the project the server returned.

## 8. Multi-step flows

Create Project is two steps because the backend is two operations that fail
independently — a Git clone can fail long after the project row exists:

    details -> POST /api/projects              -> Created
    source  -> POST /api/projects/{id}/source  -> Analyzing

Rules for any such flow:

* **A failed step never repeats a step that succeeded.** The created project
  is held in the controller, and its presence short-circuits the create call,
  so a retry of step 2 cannot produce a second project.
* **Nothing the operator typed is discarded** on failure, including on a
  server rejection.
* **A `409` that means "already in the state you wanted" is success.** The
  source step treats it as done and re-reads the project so the screen shows
  the real status.
* **The form validates on submit**, via a `Form` key, so pressing the button
  on an untouched field says what is missing instead of doing nothing.
* **Client validation mirrors the server exactly** and says so in the helper
  text — including the one rule `POST /api/projects` does not enforce but
  `start` does: a project with no description can never be started, so the
  description is required here rather than producing a project that is dead on
  arrival.
* **A partially created project is still returned** to the caller. It exists
  on the server, so the list must show it; the row offers a resume path rather
  than becoming a dead end.

## 9. Dates and times

The data layer guarantees UTC — including for the read paths where this
backend drops the `Z` ([`flutter-api-client.md`](flutter-api-client.md) §5).
The UI must not undo that.

* Widgets never subtract `DateTime`s. `RelativeTime.format` is the only
  implementation, so the rounding rules and the UTC handling cannot drift.
* Under a minute reads "Just now", including a timestamp slightly in the
  **future**: client and server clocks differ, and "in 3 seconds" is noise.
* Past seven days it falls back to an absolute localized date, because a
  relative label stops being useful there.
* A relative label is always paired with the absolute timestamp in a tooltip
  or a semantic label.
* Absolute formatting goes through `Formatters`, so German renders
  `19.09.2026, 14:03` without any screen knowing about it.

## 10. Localization

Covered in full by [`flutter-localization.md`](flutter-localization.md). What
matters at the widget level:

* No user-visible literal. Every string is a key under `common.*`, `state.*`,
  `error.*`, `validation.*`, `time.*`, `dashboard.*`, `projects.*`,
  `project.*`, `nav.*`, `settings.*` or `enums.*`.
* Operator data is **never** translated: project names, slugs, ids, model
  names, backend enum wire values, server error text, timestamps from the API.
* A validator returns a translation **key**; the widget resolves it. The
  controller stays free of presentation concerns.
* Positional `{}` placeholders must match in count between `en.json` and
  `de.json` — a German string with fewer placeholders silently drops a value.
  A test asserts this.
* `test/localization/translation_coverage_test.dart` scans all of `lib/` for
  `'key'.tr(` and fails if either language is missing it, which covers the
  branches no widget test happens to render.

## 11. Accessibility

* Every status chip carries a `Semantics` label prefixed with the field name,
  so a screen reader announces "Status: Human review" rather than a bare word.
* Every list row is announced as a button with its name and state.
* Buttons keep a 44 px minimum height (set once in `AppTheme`), which is also
  what makes them comfortable pointer targets on desktop.
* Tooltips on every icon-only control, so the refresh action is reachable and
  legible by keyboard.
* Loading and error regions are `liveRegion: true`, so a state change is
  announced rather than silently replacing the screen.
* Colour is never the only signal (§3).
* Language switching stays in the Settings screen's normal focus order.

## 12. Controllers

    Widget -> Controller -> Repository interface -> ApiClient -> Dio

A widget never touches Dio, a URL, a DTO or a database, and a controller never
touches Dio. Controllers resolve repositories from GetX by **interface**, so a
test substitutes a fake without the controller knowing.

Three conventions worth keeping:

* **Screen controllers are bound per route**, in `AppRoutes.pages`. GetX
  disposes them when the route is left, and `onClose` cancels the in-flight
  `RequestCancellation` so a late response cannot write into a dead
  controller. Repositories stay in the global container.
* **The reload method is `refreshData()`, not `refresh()`.** `GetxController`
  already has a `refresh()` that notifies `GetBuilder` listeners; shadowing it
  with an async reload is a trap.
* **Observables must be read inside the `Obx` closure.** A child widget's
  `build` runs outside that scope, so GetX does not track what it reads there
  — and an `Obx` whose closure reads nothing throws "improper use of a GetX"
  at runtime. Either read the values in the closure and pass them down as
  parameters (`_Toolbar(filter: controller.filter.value)`), or build the
  subtree with plain functions called from inside the closure, as
  `create_project_dialog.dart` does.

## 15. Project header and facts (Phase 19.4)

`ProjectHeaderPanel` is the identity block of a detail screen: name as the card
title, the project-level action as its trailing control, and the fields the API
actually returns laid out as `Fact`s.

* **Operator data is rendered verbatim.** Name, slug, id, description, branch
  and repository are never translated, never title-cased and never shortened.
  Only the *status word* is localized, over an untouched wire value.
* **The id is selectable.** It is the join key into the audit log and into a
  server log line, so the operator can copy it.
* `FactList` gives each fact a full row when compact and a 240 dp column
  otherwise — sized for German labels, which run 20–35 % longer.
* An action that is **not** offered says why, through `UnavailableNote`, when
  the reason is not visible elsewhere on the screen. A project in `Created`
  gets "the source has not been initialized"; a project that has already
  started gets nothing, because its "Lifecycle started" timestamp is the
  explanation.

## 16. Lifecycle visualization (Phase 19.4)

`ProjectLifecycleWidget` renders `ProjectLifecycle`, a pure projection of one
project. Both are built around a single distinction, and it is the one thing a
later phase must not quietly drop:

> The API reports **one status and no history**. A stage other than the
> reported one can be placed *before* or *after* it. It can never be called
> *completed*.

* The track is the forward chain in
  `AI.Factory.Domain.Entities.ProjectTransitions` —
  `Created → Analyzing → Specification → Architecture → Planning →
  Implementation → Testing → Reviewing → HumanReview → Completed`. No edge is
  invented, and no stage carries a timestamp (`G-STAGE-TIMESTAMPS`).
* Positions are `earlier`, `current`, `anchor`, `later`, `undetermined` —
  deliberately not `completed` / `pending`. A stage before the current one was
  necessarily entered, because the transition table admits no way past it, but
  *when* is unknown; a stage after it may still have been visited already,
  because `HumanReview → Implementation` is a legal rework branch.
* `Repairing` is the one off-track state the table pins to a stage: it is
  entered only from `Testing` and leaves only to `Testing`, so `Testing` is
  marked `anchor` and **no** stage is marked current.
* `Paused`, `Failed` and `Cancelled` overwrite `status` on the backend, so the
  stage they interrupted is unrecoverable from the API. Every stage then reads
  `undetermined` and the card states, in words, that the stage is not exposed.
  It never guesses one.
* The reported state is the visually dominant element, and it is stated in
  **words** as well as colour: "Current state · Implementation · Current
  stage". Each track node's semantics label reads "Step 6 of 10:
  Implementation. Before the current stage", so the track is usable without
  seeing it.
* The track is a `Wrap` and every stage label is `Flexible`. A ten-stage track
  with German labels has to wrap and must never clip: a truncated stage name is
  a misread lifecycle.

## 17. Source, and what is never displayed (Phase 19.4)

`GET /api/projects/{id}` exposes `gitRepository` and `gitBranch` and no
source-type field, so `ProjectSourceView` derives three cases and claims
nothing beyond them: **not initialized** (the project is still `Created`, so no
source decision has been recorded), **empty workspace** (initialized, no
repository), **git repository**.

A repository URL can carry a credential — an operator may paste
`https://x-access-token:ghp_…@github.com/o/r.git` into the create wizard, and
the backend stores the string it was given. `ProjectSourceView.redactCredentials`
strips the userinfo *before the value reaches the widget tree*, so no token can
reach a screenshot or a bug report. A bare `user@host` is kept: it is a
username, not a secret. SSH (`git@host:org/repo.git`) and local paths are
untouched.

## 13. How these rules are held in place

Each rule above has a test, so a later phase that breaks one gets a failure
rather than a review comment:

| Rule | Test |
|---|---|
| No screen renders a blank frame; every state has a view | `test/widget/*_page_test.dart` — a case per state |
| Empty ≠ failed ≠ filtered-to-nothing | `projects_page_test.dart`, `projects_controller_test.dart` |
| Not-available ≠ zero; a 401 is not an error | `dashboard_page_test.dart`, `dashboard_controller_test.dart` |
| One press, one request | `projects_controller_test.dart`, `create_project_controller_test.dart`, and the disabled-button assertions in the widget tests |
| A failed step never repeats a successful one | `create_project_controller_test.dart` |
| Client validation mirrors the backend | `create_project_controller_test.dart`, checked against `Program.cs` |
| Status labels are localized, wire values never shown | `projects_page_test.dart`, `enum_labels_test.dart` |
| No German layout overflows | a per-breakpoint German test on every screen |
| No untranslated key reaches the UI | `expectNoUntranslatedKeys` plus `translation_coverage_test.dart` over all of `lib/` |
| Relative time is UTC-correct and localized | `relative_time_test.dart` |
| Sorting and filtering are client-side and stated as such | `project_list_filter_test.dart` |
| No lifecycle stage is ever called completed; an off-track state marks none | `project_lifecycle_test.dart`, `project_detail_page_test.dart` |
| A mutation publishes only the status the server returned | `project_detail_controller_test.dart` |
| `404` is a not-found state, not a retryable failure | `project_detail_controller_test.dart`, `project_detail_page_test.dart` |
| No repository credential reaches the widget tree | `project_lifecycle_test.dart`, `project_detail_page_test.dart` |
| A malformed dependency edge is reported, never silently dropped, and never crashes the tree | `dependency_graph_validation_test.dart`, `work_items_section_test.dart` |
| A plan 404 falls back to a flat list, never an inferred edge | `work_items_controller_test.dart`, `work_items_section_test.dart` |
| Readiness is fetched lazily, one call, for the selected item only | `work_items_controller_test.dart` |

And the flow as a whole, in the real application against a live API:
`integration_test/operator_flow_test.dart`,
`integration_test/project_detail_flow_test.dart`,
`integration_test/offline_flow_test.dart` and
`integration_test/work_items_flow_test.dart` — see
[`flutter-architecture.md`](flutter-architecture.md) §10 for why those cannot be
widget tests.

## 18. Work items and the dependency tree (Phase 19.5)

`WorkItemTile` is the list row: identifier (plan key if one exists, otherwise
the work item id), title, `StatusChip.workItem`, the readiness chip when a
plan supplied one, and the attempt/repair counters as plain text — nothing
this row shows is a field `WorkItemDto` does not return. A "shared" icon
marks a work item with more than one valid dependency, which is how it
appears under more than one parent in the tree below.

`WorkItemDetailPanel` adds description, the selected item's live readiness
(fetched on selection, shown as a small spinner while in flight), type,
priority, its direct dependencies resolved to titles, the recorded error if
one exists, and an explicit `UnavailableNote` for acceptance — `G-ACCEPTANCE`
is still unresolved, so "Completed" is never presented as "acceptance
passed".

`WorkItemDependencyTree` draws the plan as rows, indented by depth, not as a
graph canvas — `docs/flutter-ux-spec.md` §8's own choice. Two safety rules
that are easy to get wrong and are each pinned down by a test:

* **Roots are computed from valid edges, not from `ProjectPlan.roots`.** A
  node whose only `dependsOnPlanKeys` entries are a self-reference or an
  unknown plan key still has a *non-empty* list, so `WorkItemNode.isRoot`
  reports `false` for it — and nothing else lists it as a dependency either.
  Trusting `.roots` directly would render that node exactly zero times, which
  is the node disappearing rather than an edge being marked invalid. The tree
  computes its own effective-roots set (every dependency edge that is either
  self-referential or points at a key the plan does not have is discounted)
  so every node still renders somewhere.
* **A cycle is a dead end, not a stack overflow.** Each row carries the set
  of plan keys already on its path from a root; a child whose key is already
  in that set is drawn once, marked with a warning icon, and not recursed
  into again.

No `DependencyLegend` widget was added: the tree is self-describing per row
(status chip, shared icon, cycle marker with a tooltip), so a separate key
was not justified by anything the tree actually needs explained.

## 19. Agent runs and execution detail (Phase 19.6)

`AgentRunTile` is the list row: the agent's name (falling back to its raw id
when the definition join misses), `StatusChip.agentRun`, the attempt number,
and whichever of `startedAtUtc`/`createdAtUtc` the run actually has — the same
"only fields the DTO returns" discipline as `WorkItemTile`. `StatusChip.agentRun`
is a new factory rather than reusing `StatusChip.workItem`: an AgentRun
`Succeeded` and its WorkItem `Completed` are different domain concepts
(Workstream G), and sharing a label would blur that distinction the backend
itself keeps separate.

`AgentRunDetailPanel` shows exactly `docs/flutter-ux-spec.md` §6.6's field
list — agent type/name, project, work item (with a "View work item" action
when the caller supplies one), status, attempt, created/started/completed,
duration, failure category when present, the correlation id with a copy
button, and the structured result's summary/output/data/warnings/errors/
artifacts/metadata, each section rendered only when the real value carries it
(Workstream I: never interpret `output` or `errorMessage` as structured
truth). Immediately below the facts sits a permanent `UnavailableNote` stating
that model, provider and fallback are not exposed by this endpoint
(`G-RUN-TELEMETRY`, `G-FALLBACK`) — Workstream N's "show unavailable rather
than guessing", applied the same way `WorkItemDetailPanel` already applies it
to acceptance results.

Filtering has two different mechanisms and the UI does not blur them: the
status dropdown is a real `AgentRunQuery.status` parameter and re-queries the
server; the work-item filter set from a WorkItem's "View agent runs" action is
client-side only, narrowing the page already in memory, because
`GET /api/agents/runs` has no `workItemId` parameter (`G-WORKITEM-EVENTS`).

No Orchestration Run Detail screen exists yet, even though
`docs/flutter-ux-spec.md` §6.7 describes one: an `OrchestrationRun` id is
discoverable only from a `POST /api/orchestration-runs` response or from the
operational-key-gated `ProjectDiagnosticSummary.latestRunId` — nothing on
`AgentRunDto` or `WorkItemDto` links to one, so there is no lineage path a
read-only AgentRuns screen can follow to it (`G-RUN-LIST`). Building the
screen without a way to reach it would be dead UI, not a feature.

## 20. Human Review and approval (Phase 19.7)

`HumanReviewSection` is self-managing off `Project.status`, the same pattern
`ProjectLifecycleWidget` already uses: `HumanReview` renders the full section
(banner, evidence, Approve), `Completed` renders a compact "Project Completed"
note, and every other status renders nothing. The section never infers
eligibility from WorkItem or AgentRun state (Workstream A) — only the
project's own status decides.

Review Summary and Execution Evidence read the sibling `WorkItemsController`/
`AgentRunsController` state already loaded for the page rather than fetching
anything of their own (Workstream AH, §19 above). Acceptance
(`G-ACCEPTANCE`) and execution telemetry (`G-RUN-TELEMETRY`) use the same
`UnavailableNote` "not available" pattern §4 already establishes — a
`Completed` WorkItem status or a `Succeeded` AgentRun is never read as
acceptance evidence.

Approve is a **consequential action** (§7): it is never a single tap.
`ApprovalConfirmationDialog` names the project by its real name, states what
approving will do, and labels its buttons `Cancel`/`Approve` — never `OK` or
`Yes` — with `Cancel` as the default-focus button, so an accidental Enter is
the safe outcome. The button itself is disabled and shows a progress
indicator while a request is in flight, and `HumanReviewController` drops a
second `approveProject()` call outright — the guard lives in the controller,
not only in the disabled button, so a stray second tap during the frame
before the widget rebuilds still cannot double-submit.

**Conflict/reload pattern:** a `409` or `404` from the approve endpoint is
shown as an `ActionErrorBanner` above the section — the evidence below it
stays on screen — and the controller reloads
`ProjectDetailController.refreshData()` so the operator sees the server's
actual current state rather than a stale local copy. `Completed` is reachable
only through that reload: the controller never sets it locally, matching the
"server response wins" rule §7 already states for `ProjectDetailController`.

## 21. Operations, diagnostics and operator access (Phase 19.8)

`OperationsSection<T>` renders one endpoint's whole state matrix — loading,
refreshing, success, empty, unauthorized, failure — in one place, with its own
retry. Operations is several independent endpoints, so the screen composes
several of these rather than wrapping the page in a single `AsyncView`: a
section that fails shows its failure *in place* while every other section keeps
its data (§1's states, applied per-section instead of per-screen). A refresh
keeps loaded values on screen, and a section that fails on refresh keeps its
previous values, dimmed, under a "showing the last values that loaded" note.

**`401` is not an error state.** It renders as "Operator access required" with
a key icon and an action that scrolls to the operator-access panel — never the
red failure treatment §5 uses for a server error, because an outage and a
missing credential ask different things of the operator.

**The operator-access panel** reports whether a key is stored and nothing else.
The field is obscured, the value is never rendered back — not even masked,
because a masked secret still leaks its length — and no error message quotes
it. Saving re-runs exactly the reads that failed; clearing drops the header
immediately, even when the keystore write itself failed.

**Health is stated at the scope that answered.** The section is "API health",
never "System health": only the API replied. Liveness and readiness each read
as a word plus an icon (`Healthy` / `Unhealthy` / `Unavailable`) rather than a
colour, and "unreachable" stays distinct from "answered, not ready". The same
rule names the scheduler section — "Scheduler activity", never "Worker
healthy" — with an explicit note that dispatch counters are not worker health
in either direction.

**Unavailable capabilities live inside the section they belong to**, as an
`UnavailableNote` (§4), never as an omission and never as a zero: worker health
and per-check detail under health, model routing under configuration,
system-wide stale runs and acceptance under diagnostics, model and cost
telemetry under agent metrics.

**Two filters, two mechanisms, said out loud.** In the audit log the project
filter is a real query parameter and re-queries the server; the severity filter
narrows the loaded page only, because the endpoint accepts `severity` and then
ignores it (`G-AUDIT-SEVERITY`). The UI says so under the control rather than
letting the operator infer a server-side filter that does not exist — the same
discipline §19 applies to the agent-run work-item filter.

## 22. Live updates (Phase 19.9)

Polling is not a connection, so the words never pretend it is. There is no
"Connected", no "real-time", no "instant". The vocabulary is *live updates*,
*refreshing*, *last updated*.

**One indicator per live screen.** `LiveStatusIndicator` sits at the top of the
content (Dashboard, Projects, Project Detail, Diagnostics, Operations) and reads
one line: an icon, a status, and `Last updated 10:42:13`.

| Status | Shown when | Offers |
|---|---|---|
| Waiting for the first update | nothing has loaded yet | — |
| Live updates on | the last refresh succeeded and another is scheduled | — |
| Refreshing… | only for the very first load | — |
| Offline · retrying automatically | network or timeout; backing off | Retry now |
| Refresh failed · retrying automatically | 5xx / 503 / 429; backing off | Retry now |
| Refresh failed · live updates stopped | 400/403/404/409/422 | Retry now |
| Access required · live updates stopped | 401 | Enter operational key (Operations) |
| Live updates paused | page covered or app in background | — |
| Not refreshing automatically — nothing here is changing | terminal project | the screen's own refresh button |

**Refresh state.** A background poll changes the icon to a small spinner and
nothing else: the words do not flicker every few seconds, the content does not
blank, and there is no progress line. The thin `LinearProgressIndicator` of §1 is
reserved for a refresh the operator asked for. A failed poll keeps the last data
on screen (§1 "stale beats blank") and says so in the indicator.

**Last updated** is absolute local wall-clock time (`Formatters.time`), because a
relative "5 seconds ago" would need a ticking timer in the widget. It moves only
on a successful, authoritative response; a failed or cancelled poll leaves it
where it was, so its age is the honest measure of staleness.

**Offline** is a state of the indicator, not a banner that replaces the screen:
the operator keeps reading the last data while the client retries with backoff,
and recovery needs no restart.

**Accessibility.** The status is a live region carrying the *settled* status —
the one that ignores a request in flight — so a routine poll is never
announced. Going offline, a failure, access required and recovery are. The
"Refreshing…" label is reachable by a screen reader but is not a live region.

**Widgets never poll.** A page wraps itself in `LiveUpdatesScope`, which only
reports visibility (mounted, covered, uncovered) to the controller's
coordinator. Timers live in `PollingCoordinator` alone
([`flutter-architecture.md`](flutter-architecture.md) §12).

## 23. Hardened patterns (Phase 19.10)

Introduced while hardening the client; every new screen follows them.

| Pattern | Rule | Where |
|---|---|---|
| Deterministic time | "Now" and local-time conversion come from `AppClock`, never `DateTime.now()`/`toLocal()` in a widget. Goldens pin both | `lib/core/utils/app_clock.dart` |
| Plural counts | A count in text is a plural key (`one`/`other`) called with `.plural(n)`; "1 attempts" is a defect the coverage test catches | `assets/translations/*.json`, `test/localization/translation_coverage_test.dart` |
| Buttons that may not fit | Dialog actions sit in an `OverflowBar`, not a `Row`: at 200 % text in German they stack instead of clipping | `create_project_dialog.dart` |
| Rows that may not fit | A row with a title, a status chip and markers measures itself (`LayoutBuilder`, width against `360 × text scale`) and moves the chips under a two-line title rather than truncating the title to a few letters | dependency tree, agent-run filter |
| Deep trees | Rows are flattened iteratively; a node's subtree is expanded once; indentation is capped at a third of the width | `dependency_graph_validation.dart`, `work_item_dependency_tree.dart` |
| Keyboard focus | Material buttons draw a 2 dp primary outline when focused (theme); custom `InkWell` tiles are wrapped in `FocusRing`. Keyboard only — a click draws nothing | `app_theme.dart`, `focus_ring.dart` |
| Escape | Escape is Cancel in both dialogs. The barrier stays non-dismissible — a stray click is not a decision — but the keyboard's back-out key is always the safe choice, never the approval | `approval_confirmation_dialog.dart`, `create_project_dialog.dart` |
| Semantics on the focusable node | The consequence of a button is announced on the node that takes focus ("Approve project — this will transition the project to Completed"), not on a wrapper | `human_review_section.dart` |
| Self-contained labels | A row names its role and every fact it shows: "Work item T03, …, Status: Running, Readiness: Running, 1 attempt"; "Agent run: …, Status: …, Attempt 1, Work item: …"; a count names its group ("Agent runs, Active: 1") | tiles, diagnostics |
| Reduced motion | `AppMotion`: with the platform's reduce-motion setting, route transitions and dialog fades are skipped and the live-status spinner becomes a still icon; the state change and its announcement stay | `app_motion.dart`, `live_updates.dart` |
| Status colours | Every tone reaches 4.5:1 as text on its own 12 % tint over every surface a chip sits on, in both themes; `neutral` is `onSurfaceVariant`, not `outline` | `app_theme.dart`, `test/accessibility/contrast_test.dart` |

## 14. What the Dashboard deliberately does not show

Listed so a later phase does not "fix" an omission that is intentional. Each
is either absent from the API or belongs to a later screen:

| Not shown | Why |
|---|---|
| Worker health | Not exposed (`G-WORKER-HEALTH`). Scheduler activity is shown instead, labelled as activity |
| Per-check health detail | `/health/*` returns plain text and names no check (`G-HEALTH-DETAIL`) |
| A global stale-run count | Stale runs are per-project only (`G-STALE-GLOBAL`) |
| Backend version, uptime, database counters | `/api/operations/deployment` and `/database` are operational-key endpoints and belong to Phase 19.8. The Platform band shows the **client** version and the backend release it was built against, labelled as such |
| Acceptance results, artifacts, run telemetry, model routing | `G-ACCEPTANCE`, `G-ARTIFACTS`, `G-RUN-TELEMETRY`, `G-ROUTING` |
| Anything pushed in real time | `G-REALTIME`. There is no push transport; since Phase 19.9 the Dashboard polls (10 s, 30 s when nothing is active) and says "Live updates", never "real-time" (§22) |
