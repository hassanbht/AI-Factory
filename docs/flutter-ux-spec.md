# Flutter Client — UX Specification

Target backend: AI Factory `v1.0.0` · Application: `apps/ai_factory_client`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-architecture.md`](flutter-architecture.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md), [`flutter-roadmap.md`](flutter-roadmap.md).

---

## 1. Application purpose

The AI Factory client is an **operator console** for an autonomous software factory.
It answers four questions, in this order of importance:

1. Does a project need me right now? (Human review, failure, blocked)
2. What is the factory doing at this moment?
3. Why did something fail, and on which attempt?
4. Is the platform itself healthy?

It is a control and observation surface. It never performs orchestration itself, never
decides a project is complete, and never bypasses the API.

## 2. Target users

| User | Needs |
|---|---|
| **Single operator** (primary) | Start projects, watch execution, approve at the human gate, diagnose failures |
| **Reviewer** | Read the human-review evidence and approve or send work back |
| **Maintainer** | Deployment, database, configuration and resilience diagnostics |

There is no end-user authentication in the backend, so there are no roles in the UI.
The operational key gates the diagnostics endpoints, nothing else.

## 3. Language and localization (English + German)

* Supported UI languages: **English (`en`)** and **German (`de`)**.
* The startup language follows the operator's saved choice; if none, the system locale
  when it is `de` or `en`; otherwise English.
* The language switcher lives in **Settings → Appearance and language**
  (implemented in Phase 19.1).
* Switching is immediate: no restart, no reload, no loss of screen state or scroll
  position; in-flight polls are not restarted.
* Dates, times, durations and numbers follow the active locale
  (`19 Sep 2026, 14:03` / `19. Sep. 2026, 14:03`; `1,234.5` / `1.234,5`).
* Relative timestamps are localized ("2 minutes ago" / "vor 2 Minuten").
* **Operator data is never translated**: project names, work item titles and
  descriptions, model names, git branches, ids and correlation ids render verbatim.
* **Backend state names are shown as localized labels** while the underlying wire value
  is unchanged. Examples:

| Wire value | English label | German label |
|---|---|---|
| `Created` | Created | Erstellt |
| `Analyzing` | Analyzing | Analyse |
| `Specification` | Specification | Spezifikation |
| `Architecture` | Architecture | Architektur |
| `Planning` | Planning | Planung |
| `Implementation` | Implementation | Implementierung |
| `Paused` | Paused | Pausiert |
| `Testing` | Testing | Test |
| `Repairing` | Repairing | Reparatur |
| `Reviewing` | Reviewing | Prüfung |
| `HumanReview` | Human review | Menschliche Freigabe |
| `Completed` | Completed | Abgeschlossen |
| `Failed` | Failed | Fehlgeschlagen |
| `Cancelled` | Cancelled | Abgebrochen |

  The same rule applies to WorkItem status, readiness, AgentRun status, failure
  categories and orchestration stages. These labels ship in
  `assets/translations/{en,de}.json` as of Phase 19.1; see
  [`flutter-localization.md`](flutter-localization.md).
* An unknown state received from a newer backend renders as
  `Unknown (RawValue)` / `Unbekannt (RawValue)` — visible, never blank.
* German text is 20–35 % longer; no layout may be sized to English text
  ([`flutter-architecture.md`](flutter-architecture.md) §9.6).

## 4. Navigation

    Dashboard
    Projects
        -> Project Detail
              Lifecycle | WorkItems | AgentRuns | Acceptance | Artifacts | Review
              -> WorkItem Detail
                    -> AgentRun Detail
              -> Orchestration Run Detail
              -> Human Review
    Operations
        -> Diagnostics
        -> Deployment
        -> Database
        -> Configuration
        -> Audit Log
    Models / Routing
    Settings

Navigation model:

* A persistent **navigation rail** (desktop and tablet width) or **bottom navigation
  bar** (compact width) for the top-level destinations. Models/Routing is folded into
  Operations until `G-ROUTING` is resolved, so the shell ships four destinations:
  Dashboard, Projects, Operations, Settings.
* Project context is carried in the route path
  (`/projects/:projectId/workitems/:workItemId/runs/:runId`), so deep links work and the
  back stack always returns to the owning project rather than to the projects list.
* A **context header** (project name + status chip) stays visible on every screen below
  Project Detail, and clicking it returns to Project Detail.
* Route arguments are ids only; every screen re-fetches from the API so a deep link or a
  refresh is always valid.

## 5. Screen inventory

| # | Screen | Primary API dependencies |
|---|---|---|
| 1 | Dashboard | `GET /api/projects`, `GET /api/agents/metrics`*, `GET /health/ready`, `GET /api/scheduler/status` |
| 2 | Projects | `GET /api/projects` |
| 3 | Create Project (wizard) | `POST /api/projects`, `POST /api/projects/{id}/source`, `POST /api/projects/{id}/start` |
| 4 | Project Detail | `GET /api/projects/{id}`, `GET /api/projects/{id}/tasks`, `GET /api/projects/{id}/plan`, `GET /api/projects/{id}/diagnostics`* |
| 5 | WorkItem Detail | `GET /api/tasks/{id}`, `GET /api/tasks/{id}/readiness`, `GET /api/agents/runs?projectId=` |
| 6 | AgentRun Detail | `GET /api/agents/runs/{id}` |
| 7 | Orchestration Run Detail | `GET /api/orchestration-runs/{id}`, `/attempts`, `/logs` |
| 8 | Human Review | `GET /api/projects/{id}` + tasks + plan + diagnostics* + orchestration attempts, `POST /api/projects/{id}/approve` |
| 9 | Operations · Diagnostics | `GET /api/operations/metrics`*, `/resilience`*, `/active`*, `/recent`* |
| 10 | Operations · Deployment | `GET /api/operations/deployment`* |
| 11 | Operations · Database | `GET /api/operations/database`* |
| 12 | Operations · Configuration | `GET /api/operations/configuration`* |
| 13 | Operations · Audit Log | `GET /api/audit-log`* |
| 14 | Models / Routing | **none — see gap `G-ROUTING`** |
| 15 | Settings | local only (+ a `GET /health/live` connection test) |

`*` requires the operational key when `RequireAccessKey` is enabled.

## 6. Screen responsibilities

### 6.1 Dashboard

Three clearly separated bands, in this order:

1. **Needs attention** — projects in `HumanReview`, then `Failed`, then those with a
   non-null `currentError`. Empty state: "Nothing needs your attention."
2. **Project state** — counts for Total / Active (`Analyzing`…`Reviewing`) / Human
   review / Failed / Completed / Paused / Cancelled, computed client-side from
   `GET /api/projects`. Each count is a filter link into Projects.
3. **System** — API health, readiness, scheduler enabled + last cycle time, queued and
   running agent runs, application version.

The layout physically separates system health from project state. A healthy platform is
never presented as evidence that a project succeeded; the system band is visually
subordinate (smaller tiles, bottom row) and labelled "Platform", not "Status".

Stale agent runs are **per-project only** (`ProjectDiagnosticSummary.staleAgentRuns`),
so the Dashboard shows a stale count only for projects whose diagnostics have already
been fetched, and labels it as such rather than implying a global figure
(gap `G-STALE-GLOBAL`).

### 6.2 Projects

Sortable, filterable table: name, status chip, lifecycle started, last updated, current
error indicator. Filters: status, "started only", free-text on name. Row → Project
Detail. Primary action: **New project**.

The endpoint returns every project with no pagination, so the list is virtualized and a
banner appears above 200 rows recommending a filter (gap `G-PROJECT-PAGING`).

### 6.3 Create Project wizard

Three steps, mirroring the only order the backend accepts:

1. **Name + description.** Description is mandatory here even though `POST /api/projects`
   allows it to be empty, because `start` rejects a project without one. Client-side
   validation matches the server exactly: ≤ 200 chars, no `..`, no invalid filename
   characters, description ≤ 2000.
2. **Source.** `Empty` or `GitRepository` (+ location, branch). On success the project
   becomes `Analyzing`.
3. **Start.** Optional actor label; calls `POST …/start`.

Each step is independently retryable, because each maps to a separate backend operation
that can fail on its own. A project stuck after step 1 is shown in Projects as
"Source not initialized" with a resume action. Steps 2 and 3 are idempotent-safe: a
`409` "already initialized" advances the wizard instead of showing an error.

### 6.4 Project Detail

Header: name, status chip, created/updated, lifecycle started, current error banner,
actions (Pause/Resume, Cancel, Approve when applicable, Refresh).

Tabs:

| Tab | Content |
|---|---|
| **Lifecycle** | The lifecycle visualization (§7) |
| **WorkItems** | Dependency tree + list (§8) |
| **AgentRuns** | Runs for this project, newest first, filterable by status |
| **Acceptance** | Acceptance criteria and results — **blocked by gap `G-ACCEPTANCE`**; renders an explicit "not exposed by the API" panel, never a fake pass |
| **Artifacts** | Artifact names from `structuredResult.artifacts` only; no browsing or download (gap `G-ARTIFACTS`) |
| **Review** | Human review summary and the approval gate (§9) |

Behaviour per project state:

| Project state | Screen behaviour |
|---|---|
| `Created` | Lifecycle shows step 1 only; prominent "Initialize source" call to action; tabs empty with guidance, not spinners |
| `Analyzing` … `Reviewing` | Active stage animated; poll 5 s; WorkItems live |
| `Paused` | Stage frozen with a paused badge; poll 30 s; Resume action offered |
| `Repairing` | Repair badge with repair attempt counts from the WorkItem list |
| `HumanReview` | Review tab auto-selected; approval banner pinned at the top |
| `Failed` | `currentError` banner; Lifecycle shows the stage at which it stopped; failed WorkItems listed first |
| `Completed` | Read-only, approval evidence retained, polling stopped |
| `Cancelled` | Read-only, "Cancelled" chip, polling stopped |

### 6.5 WorkItem Detail

Title, type, status, priority, attempt count (`attemptCount` / max is not exposed —
displayed as a bare number, not a fraction, gap `G-MAXATTEMPTS`), repair attempts,
readiness (fetched on open), current error, last heartbeat, timestamps, dependencies
(from the plan), and the agent runs attributed to this work item.

### 6.6 AgentRun Detail

Displays: agent definition (name + type), project, work item, status, attempt,
created/started/completed, duration, failure category, error message, correlation id
(copyable), and the structured result — summary, output, data, warnings, errors,
artifacts, metadata.

**Model and provider are not returned by this endpoint.** The UI shows "Not exposed by
the API" for those fields and links to the orchestration run, which does expose
`selectedModel` (gaps `G-RUN-TELEMETRY`, `G-FALLBACK`).

Never displayed, under any circumstance: API keys, authorization headers, provider
credentials, Hermes credentials, system prompts, workspace paths. The API does not
return them; the client additionally has no field for them.

### 6.7 Orchestration Run Detail

Status, current stage, attempt / max attempts, selected model, timings, failure
category, plus three views: **Trace** (steps), **Attempts** (agent/build/test/
validation/review summaries per attempt, the core failure-diagnosis view) and **Log**
(progress events on a timeline). Reachable from `ProjectDiagnosticSummary.latestRunId`
or from a run just started; there is no list endpoint (gap `G-RUN-LIST`).

### 6.8 Operations

Health, project diagnostics, deployment, database, configuration, resilience, active
operations, audit log. Configuration renders only booleans, names and counts — the
snapshot contains no secret values by contract, and the UI has no field capable of
displaying one.

When the operational key is missing or wrong, every operational pane shows one shared
"Operational key required" state with a link to Settings — not eleven separate errors.

### 6.9 Models / Routing

The backend exposes no routing endpoint. This screen ships as an explicit, honest
placeholder: it states that model routing is configured server-side and is not
currently readable through the API, shows `selectedModel` values observed on
orchestration runs, and links to gap `G-ROUTING`. It never displays a fabricated
routing table.

### 6.10 Settings

Base URL (with a **Test connection** action against `/health/live`), operational key
(masked, stored in secure storage), **language (English / Deutsch)**, theme, polling
intervals, and an about panel sourced from `/api/operations/deployment`.

## 7. Lifecycle visualization

Rendered from the backend's own transition table, never from an invented one:

    Created -> Analyzing -> Specification -> Architecture -> Planning ->
    Implementation -> Testing -> Reviewing -> HumanReview -> Completed
                          Testing <-> Repairing
                          any active stage <-> Paused
                          any non-terminal stage -> Failed | Cancelled

Rules encoded in the UI:

* `Completed` is reachable **only** from `HumanReview`, and only through approval.
* `Repairing` returns to `Testing`, never forward.
* `HumanReview` may also go back to `Implementation` (rework) — shown as a legal branch.
* `Paused` is a side state, drawn beneath the main track, resumable to the stage it
  came from.
* Terminal states end the track.

Visual language: completed stages filled with a check; the active stage pulsing with a
progress affordance and elapsed time; future stages outlined and muted; `Failed` and
`Cancelled` as a red/grey terminator on the stage where execution stopped; `Repairing`
as a loop badge carrying the aggregate repair attempt count.

Timestamps: only `createdAtUtc`, `updatedAtUtc` and `lifecycleStartedAtUtc` exist at the
project level, so per-stage entry times are shown **only** where the audit log supplies
them (`WorkflowStateChanged`, operational key required). Without the key, stages carry
no invented timestamps (gap `G-STAGE-TIMESTAMPS`).

## 8. WorkItem / DAG visualization

First implementation is a **dependency tree**, not a graph canvas — no graph library.

    T01  Analysis            Completed   1 attempt
     └─ T02  Implementation  Running     2 attempts · 1 repair
         ├─ T03  Test        Ready
         └─ T04  Review      WaitingForDependency

Each row shows: plan key (or short id), title, type, status chip, readiness chip,
attempt count, repair count, and an acceptance placeholder pending `G-ACCEPTANCE`.

* Edges come from `GET /api/projects/{id}/plan` (`dependsOn` plan keys → `workItemId`).
* Live status comes from `GET /api/projects/{id}/tasks`.
* A work item appearing under several parents is rendered once per parent with a
  "shared" marker, so a DAG remains readable as a tree.
* No plan (`404`): flat list plus an explicit "No dependency plan exists for this
  project" note. Edges are never guessed.
* Readiness is fetched lazily, for the selected row only.

A graph canvas is an optional later enhancement, not a Phase 19 requirement.

## 9. Human review flow

Entry: `project.status == HumanReview`, surfaced on the Dashboard ("Needs attention"),
in Projects, and by auto-selecting the Review tab in Project Detail.

The screen states plainly, in the active language:

> **EN** — The Factory has finished its automated work. Human approval is required.
> **DE** — Die Factory hat ihre automatische Arbeit abgeschlossen. Eine menschliche Freigabe ist erforderlich.

Evidence shown, each from a named source, and each labelled "not available" rather than
omitted when its source is missing:

| Evidence | Source |
|---|---|
| Project summary | `GET /api/projects/{id}` |
| Completed work items | `GET /api/projects/{id}/tasks` (+ plan for ordering) |
| Acceptance results | **gap `G-ACCEPTANCE`** — shown as unavailable |
| Build / test result | `GET /api/orchestration-runs/{latestRunId}/attempts` |
| Repair history | `repairAttemptCount` per work item + attempt summaries |
| Review result | `reviewResultSummary` on the latest attempt |
| Errors and warnings | `currentError`, `structuredResult.warnings/errors` |
| Changed artifacts | `structuredResult.artifacts` names only (gap `G-ARTIFACTS`) |
| Approval audit | `GET /api/audit-log?projectId=…` filtered to `HumanApproval` (ops key) |

Actions:

* **Approve** — enabled **only** when the fetched status is `HumanReview`. Opens a
  confirmation dialog naming the project, requires an actor label, then calls
  `POST /api/projects/{id}/approve`. On `200`: success state, status becomes
  `Completed`, polling stops. On `409`: the project moved on — reload and explain, never
  retry silently. The button is disabled, with the reason shown, in every other state.
* **Send back to implementation** — `PATCH /status` with `Implementation`, the one
  rework transition the backend permits from `HumanReview`.
* **Cancel project** — `PATCH /status` with `Cancelled`, behind a typed confirmation.

The client never sets `Completed` through the status endpoint (the backend rejects it by
design) and implements no client-side completion mechanism of any kind.

## 10. Diagnostics flow

1. Dashboard shows a degraded platform indicator (readiness, scheduler, circuit states).
2. Operations → Diagnostics shows metrics, resilience, active operations, recent events.
3. A failing project: Project Detail → error banner → diagnostics summary → latest run →
   attempts → the failing stage → the correlation id.
4. The correlation id opens `GET /api/operations/{correlationId}`, the full timeline of
   that one operation, and can be copied for server-side log correlation.

Failure categories are rendered as localized labels with a short explanation, so
`ProviderFailure` reads as "Model provider failure — an external provider was
unavailable" / "Anbieterfehler — ein externer Anbieter war nicht verfügbar".

## 11. UX states

Every screen implements all of these; a blank screen is a defect.

| State | Rule |
|---|---|
| **Loading** | First load only: skeletons that match the final layout, never a bare spinner. Long operations show stage text ("Cloning repository…"), not an indefinite bar. |
| **Refreshing** | Data stays on screen; a thin progress line plus "Updated <relative time>" in the header. |
| **Empty** | Explains why it is empty and the next action ("No work items yet — the plan is generated after the project starts"). |
| **Success** | Content plus last-updated time. |
| **Partial** | Aggregate screens render what loaded and mark the failed panes individually with a retry on the pane. |
| **Error** | Localized message from `error.code`, a retry action, a collapsed technical detail block (endpoint, status, `detail`, correlation id — copyable). No stack traces. |
| **Retrying** | Backoff is visible: "Retrying in 12 s" plus a retry-now action. |
| **Offline / disconnected** | A persistent banner, cached data shown as stale with its age, polling suspended, automatic resume on reconnect. |
| **Forbidden / key required** | `401`/`403` on operational panes → one shared "Operational key required" state with a link to Settings. |
| **Not found** | `404` → "This project no longer exists" plus a route back to the list; never an empty detail shell. |
| **Rate limited** | `429` → "Slowing down updates" indicator, interval raised to 60 s, manual refresh still available. |

## 12. Responsive behaviour

| Breakpoint | Layout |
|---|---|
| **≥ 1024 dp — Expanded / desktop (primary)** | Extended navigation rail with labels + three-column Project Detail (lifecycle · work items · selected item detail). Dashboard is a 4-column tile grid. Tables show all columns. |
| **600–1023 dp — Medium / tablet** | Icon-only navigation rail with tooltips + two columns: master list and detail pane. Dashboard 2 columns. Secondary table columns drop first (`updatedAt`, `priority`). |
| **< 600 dp — Compact** | Bottom navigation bar (four destinations fit, and it stays reachable by keyboard), single column, detail opens as a full page. Tables become cards. Content width is capped at 1440 dp on very wide displays so text lines stay readable. |

One widget tree, driven by `LayoutBuilder` + the `WindowSizeClass` breakpoints
(Material 3: 600 dp / 1024 dp). No per-platform application, no per-platform
screen. All layouts are verified at German string lengths.

## 13. Accessibility

* All interactive elements carry localized semantic labels; icon-only buttons always
  have a tooltip and a semantics label.
* Status is never conveyed by colour alone — every status chip carries an icon and text.
* Contrast meets WCAG AA in both light and dark themes, including the status palette.
* Full keyboard operation on desktop: tab order follows reading order, `Enter`/`Space`
  activate, `Esc` closes dialogs, visible focus rings everywhere. Shortcuts:
  `Ctrl+R` refresh, `Ctrl+K` project search.
* Text scales to 200 % without clipping or overlap; no fixed-height text containers.
* Animations respect the platform "reduce motion" setting; the lifecycle pulse degrades
  to a static badge.
* Screen-reader announcements for state changes that matter ("Project moved to Human
  review"), in the active language.

## 14. API dependency summary

Every screen above maps to a real endpoint in
[`flutter-api-contract.md`](flutter-api-contract.md), except:

* **Models / Routing** — no backing endpoint (`G-ROUTING`); ships as a documented placeholder.
* **Acceptance tab** — no backing endpoint (`G-ACCEPTANCE`); ships as an explicit "not available" panel.
* **Artifacts tab** — names only from `structuredResult.artifacts` (`G-ARTIFACTS`).
* **Per-stage lifecycle timestamps** — audit-log-derived only (`G-STAGE-TIMESTAMPS`).
* **Global stale run count** — per-project only (`G-STALE-GLOBAL`).

No screen invents data, and no screen silently substitutes a client-side computation for
a backend authority.
