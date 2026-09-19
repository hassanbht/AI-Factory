# Flutter Client — Implementation Roadmap

Target backend: AI Factory `v1.0.0` · Application: `apps/ai_factory_client`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-architecture.md`](flutter-architecture.md),
[`flutter-ux-spec.md`](flutter-ux-spec.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md).

Phase numbering follows the operator's revision of 2026-09-19: localization is
folded into the foundation phase rather than occupying one of its own, and live
updates move to 19.9.

| Phase | Scope | State |
|---|---|---|
| 19.0 | API contract, UX spec, architecture, API gaps, roadmap | ✅ complete |
| **19.1** | **Flutter foundation + easy_localization EN/DE + theme + routing + Dio + GetX + responsive shell** | ✅ complete |
| **19.2** | **API client + DTOs + domain models + repositories** | ✅ complete |
| 19.3 | Dashboard + Projects | next |
| 19.4 | Project Detail + Lifecycle | |
| 19.5 | WorkItems + DAG | |
| 19.6 | Agent Runs | |
| 19.7 | Human Review + Approval | |
| 19.8 | Operations + Diagnostics | |
| 19.9 | Live updates / polling | |
| 19.10 | Testing + hardening | |
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

## 19.3 — Dashboard and Projects

**Depends on:** 19.2.
**Delivers:** the Dashboard's three separated bands (needs attention · project
state · platform), client-side status aggregation, health and scheduler tiles;
the Projects table with filters, sorting and virtualization; the Create Project
wizard implementing the mandatory `create → source → start` order with per-step
retry and idempotent `409` handling.
**Done when:** a project can be created, sourced and started end to end against a
live backend, in both languages.

## 19.4 — Project Detail and lifecycle

**Depends on:** 19.3.
**Delivers:** the Project Detail shell (header, actions, tabs); the lifecycle
visualization driven by the backend transition table, including `Paused` and the
`HumanReview → Implementation` rework branch; the per-state behaviour matrix from
[`flutter-ux-spec.md`](flutter-ux-spec.md) §6.4. A simple manual refresh is enough
here — automatic polling is 19.9.
**Done when:** the screen is correct for a newly created, running, paused,
repairing, failed, human-review, completed and cancelled project.

## 19.5 — WorkItems and the dependency tree

**Depends on:** 19.4.
**Delivers:** the `/tasks` ∪ `/plan` join; the DAG-as-tree renderer with
shared-node markers; lazy per-item readiness; WorkItem Detail; the explicit "no
dependency plan" state; the acceptance placeholder pending `G-ACCEPTANCE`.
**Done when:** a multi-level plan renders correctly and a project without a plan
renders a flat list with the stated reason — no edge is ever inferred.

## 19.6 — Agent runs

**Depends on:** 19.5.
**Delivers:** the project AgentRuns tab (paged, status-filtered); AgentRun Detail
with the structured result; explicit "not exposed by the API" fields for model,
provider and fallback (`G-RUN-TELEMETRY`, `G-FALLBACK`); Orchestration Run Detail
with Trace, Attempts and Log views; cancellation for both run kinds.
**Done when:** a failing run can be diagnosed down to the failing stage and its
correlation id, and no credential-bearing field exists in the widget tree.

## 19.7 — Human Review and approval

**Depends on:** 19.6 — the attempt summaries are the review evidence.
**Delivers:** the Human Review screen with every evidence source, each labelled
"not available" rather than omitted; the localized "automated work finished,
human approval required" statement; **Approve** enabled only in `HumanReview`,
with a confirmation dialog, an actor label and `409` reload handling; "Send back
to implementation"; "Cancel project".
**Done when:** approval succeeds against a live `HumanReview` project, the button
is provably disabled in every other state, and no client-side completion path
exists.

## 19.8 — Operations and diagnostics

**Depends on:** 19.7 (and on the operational-key input, added here together with
`flutter_secure_storage`).
**Delivers:** diagnostics, deployment, database, configuration, resilience, active
operations and the audit log; the shared "Operational key required" state; the
correlation-id drill-down; the derived, clearly labelled scheduler-activity
indicator (`G-WORKER-HEALTH`); the documented Models/Routing placeholder
(`G-ROUTING`).
**Done when:** every operational pane renders with and without a key, and no
configuration value capable of holding a secret is displayed.

## 19.9 — Live updates and polling

**Depends on:** 19.8.
**Delivers:** the shared `PollingController` — per-screen intervals, backoff,
terminal-state stop, `429` slow mode, app-lifecycle suspension, route-exit
cancellation ([`flutter-architecture.md`](flutter-architecture.md) §12); offline
banner with stale-data ages and automatic resume.
**Done when:** polling provably stops on every terminal state and the whole client
stays inside the `GeneralApi` and `OperationalApi` rate-limit budgets.

## 19.10 — Testing and hardening

**Depends on:** 19.9.
**Delivers:** the full test matrix from
[`flutter-architecture.md`](flutter-architecture.md) §10, including goldens in
both locales; accessibility pass (keyboard operation, focus order, semantic
labels, contrast, 200 % text scaling, reduce motion); the contract-fixture
regeneration script in CI; a secret scan over `lib/`; performance work on large
lists; a soak test against the rate limits.
**Done when:** CI is green, the soak test shows no `429`, and no fixture drift is
outstanding.

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
    19.10 Testing + hardening
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
