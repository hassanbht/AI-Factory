# Flutter Client — Architecture Specification

Target backend: AI Factory `v1.0.0` · Application: `apps/ai_factory_client`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md), [`flutter-ux-spec.md`](flutter-ux-spec.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-ui-patterns.md`](flutter-ui-patterns.md),
[`flutter-roadmap.md`](flutter-roadmap.md).

Written in Phase 19.0 as a specification; sections 3, 4 and 9 were updated in
Phase 19.1, sections 4, 5, 6, 8 and 11 in Phase 19.2, and sections 4 and 7 in
Phase 19.3, to describe what was actually built. The implemented data layer is
documented in detail in [`flutter-api-client.md`](flutter-api-client.md), and
the implemented UI conventions in
[`flutter-ui-patterns.md`](flutter-ui-patterns.md). Section 12 (live updates)
was rewritten in Phase 19.9 to describe the controlled polling that was built.

---

## 1. Platform targets

| Target | Priority | Notes |
|---|---|---|
| Windows Desktop | **Primary** | The single-operator console. Ships as the reference build. |
| Web | Secondary | Requires `Security:Cors` enabled with an explicit origin allowlist. |
| Tablet | Secondary | Same codebase, adaptive layout; no separate application. |

One application, one codebase, adaptive layouts. No platform forks.

---

## 2. Layering

    Presentation   (Widgets, pages, adaptive layout)
        |
    ViewModel      (GetxController + reactive UI state)
        |
    Domain         (immutable models, enums, invariants, use-case services)
        |
    Repository     (orchestrates API calls, maps DTO -> Domain, caches, polls)
        |
    ApiClient      (Dio, DTOs, JSON, error translation)
        |
    AI Factory REST API

Rules:

1. Widgets never import `dio`, never see a DTO, never see an HTTP status code.
2. Repositories never return DTOs and never throw `DioException`; they return domain
   models or throw an `AppError` (§8).
3. Domain models never carry JSON annotations and never carry `Rx` types.
4. The ViewModel layer is the only layer allowed to hold `Rx` state.
5. Dependency direction is strictly downward. A lower layer never imports an upper one.

This mirrors the backend's own Clean Architecture separation, and satisfies the
Phase 19.0 requirement that backend DTOs stay separate from UI/domain models.

---

## 3. Technology decisions

| Concern | Choice | Responsibility |
|---|---|---|
| UI | Flutter (stable channel) | Rendering, adaptive layout |
| Language | Dart 3 | Sealed classes for state and errors |
| Pattern | MVVM | View ⟷ ViewModel ⟷ Domain |
| State | **GetX** | Controllers, reactive state, DI (`Get.put`/`Get.find`), routing, runtime locale switching |
| HTTP | **Dio** | Client, interceptors, timeouts, cancellation |
| Serialization | `json_serializable` + `build_runner` | DTO ⟷ JSON, compile-time safe |
| Localization | **`easy_localization` 3.0.8** (+ `intl` for formats) | Translated strings, runtime switching, saved locale (§9) |
| Equality | `equatable` | Value equality on domain models |
| Logging | `logger` | Client-side diagnostics only |
| Testing | `flutter_test`, `mocktail`, `http_mock_adapter` | Unit, ViewModel, widget, repository contract tests |

No second state-management framework, no second HTTP client, no second
localization framework, and no code generation beyond `build_runner`. GetX is
used for state, DI and navigation; **it is not the localization engine** — see
§9 and [`flutter-localization.md`](flutter-localization.md).

---

## 4. Folder structure

As built through Phase 19.3 (`apps/ai_factory_client`):

    lib/
      main.dart                     # ensureInitialized -> services -> runApp
      app/
        app.dart                    # AiFactoryAppRoot (EasyLocalization) + GetMaterialApp
        app_routes.dart             # route names + GetPages
        app_bindings.dart           # composition root (pre-runApp + lazy)
      core/
        config/app_config.dart      # environment + API base URL from --dart-define
        errors/                     # AppError hierarchy + ErrorMapper
        localization/               # LocaleService, LocalePreference, EnumLabels
        network/                    # ApiClient (Dio), ApiHeaders, interceptors/
        responsive/breakpoints.dart # WindowSizeClass + ResponsiveBuilder
        state/view_state.dart       # the sealed ViewState<T>
        theme/                      # AppTheme, StatusPalette, AppSpacing, ThemeService
        utils/formatters.dart       # locale-aware date/number/duration
      data/
        api/api_endpoints.dart      # every API path, in one place
        dto/                        # json_serializable DTOs + converters
        mappers/mappers.dart        # pure DTO -> domain functions
        repositories/               # six API-backed implementations
      domain/
        enums/backend_enums.dart    # backend enums + unknown fallback
        models/                     # Project, WorkItem, AgentRun, operations
        repositories/repositories.dart  # the six interfaces
      presentation/
        shell/                      # AppShell, ShellDestination
        controllers/                # one GetxController per screen
        models/                     # presentation-only types (snapshot, filter, sort)
        pages/                      # dashboard, projects, project detail, operations, settings
        dialogs/                    # create_project_dialog
        widgets/                    # state views, panels, status chip, project row
    assets/translations/{en,de}.json
    test/{app,core,data,localization,presentation,widget,support,fixtures}/

`core/network/request_cancellation.dart` carries the cancellation handle that
keeps the repository interfaces free of a Dio type.
`core/localization/relative_time.dart` is the only implementation of "2 minutes
ago", so the rounding and UTC rules cannot drift between screens.

`presentation/models/` holds types that exist for the UI and have no backend
counterpart: the Dashboard's assembled snapshot and its client-side counts, the
Projects screen's filter, and the presentation sort order. They are not domain
models and must not grow API knowledge.



---

## 5. Domain models

Immutable, `const`-constructible where possible, `Equatable`, no JSON, no `Rx`.

| Model | Source endpoint(s) |
|---|---|
| `Project` | `/api/projects`, `/api/projects/{id}` |
| `WorkItem` | `/api/projects/{id}/tasks`, `/api/tasks/{id}` |
| `WorkItemNode` | `WorkItem` + plan `dependsOn` edges (client-composed) |
| `ProjectPlan` | `/api/projects/{id}/plan` |
| `AgentRun` | `/api/agents/runs` |
| `AgentDefinition` | `/api/agents/definitions` |
| `StructuredAgentResult` | nested in `AgentRun` |
| `OrchestrationRun`, `OrchestrationAttempt`, `ProgressEvent` | `/api/orchestration-runs/{id}[/attempts\|/logs]` |
| `ProjectDiagnostics` | `/api/projects/{id}/diagnostics` |
| `DeploymentInfo`, `DatabaseInfo`, `ConfigurationInfo` | `/api/operations/{deployment,database,configuration}` |
| `SchedulerStatus` | `/api/scheduler/status` |
| `AgentMetrics` | `/api/agents/metrics` |
| `AuditEntry` | `/api/audit-log`, `/api/operations/recent` |
| `SystemHealth` | `/health/ready` + `/health/live` (client-composed) |
| `DashboardSummary` | client-composed aggregate (§6) |

Implemented in Phase 19.2: `Project`, `WorkItem`, `WorkItemNode`, `ProjectPlan`,
`AgentDefinition`, `AgentRun`, `StructuredAgentResult`, `AgentMetrics`,
`PagedResult`, `ProjectDiagnostics`, `DeploymentInfo`, `DatabaseInfo`,
`ConfigurationInfo`, `ProviderInfo`, `SchedulerStatus`, `SystemHealth`.

Models the backend does not expose, and which are therefore **not** defined:
`AcceptanceCriterion`, `Approval`, `Artifact`, `ModelRoute`. They stay in
[`flutter-api-gaps.md`](flutter-api-gaps.md) until an API phase adds them.
`OrchestrationRun` and `AuditEntry` are deferred to the phases that display
them (19.6 and 19.8) rather than built speculatively.

### Enum policy

Every backend enum is mirrored as a Dart enum **plus an `unknown` member**. Parsing is
`fromWire(String)`, case-insensitive, never throwing; an unrecognized value becomes
`unknown` and is logged once. This keeps an older client alive against a newer backend.

`WorkItem.status` arrives as a free-form string, so it always goes through the same
defensive parse.

**Wire values are never translated.** An enum's wire name (`HumanReview`, `Repairing`,
`ProviderFailure`) is an identifier; only its *display label* is localized (§9).

### Conversion chain

    JSON  ->  DTO (json_serializable)
          ->  Domain model (mapper, defensive enum parse, UTC -> DateTime)
          ->  ViewModel state (Rx<ViewState<T>>)
          ->  Widget (localized labels, formatted dates)

Mappers are pure functions in `data/mappers/`, unit-tested against captured real
response fixtures.

---

## 6. Repositories

One repository per bounded area: `ProjectRepository`, `WorkItemRepository`,
`PlanRepository`, `AgentRunRepository`, `OrchestrationRepository`,
`DiagnosticsRepository`, `HealthRepository`, `AuditRepository`.

Responsibilities:

* call one or more endpoints,
* map DTO → domain,
* translate transport failures into `AppError`,
* compose client-side aggregates the API does not provide.

Composition rules that matter:

* **`DashboardSummary`** = `GET /api/projects` (counts by status, computed client-side)
  + `GET /api/agents/metrics` (queued/running) + `GET /health/ready`
  + `GET /api/scheduler/status`. Four calls, one refresh cycle, never more often
  than every 15 s.
* **WorkItem DAG** = `GET /api/projects/{id}/tasks` ∪ `GET /api/projects/{id}/plan`.
  The plan supplies the edges (`dependsOn` plan keys → `workItemId`); the task list
  supplies live status/attempt data. When the plan is `404`, the UI renders a flat
  list and states that no dependency graph exists yet — it never fabricates edges.
* **Readiness** is per-task (`GET /api/tasks/{id}/readiness`) and is therefore fetched
  lazily, only for the expanded/selected WorkItem — never in a loop over the list.

Repositories are stateless apart from a short-lived in-memory cache keyed by
`(endpoint, args)` with a 2 s TTL, to collapse duplicate reads triggered by multiple
widgets in the same frame.

---

## 7. ViewModel state

A single sealed view state is used everywhere:

    sealed class ViewState<T> {}
    class Idle<T>      extends ViewState<T> {}
    class Loading<T>   extends ViewState<T> {}          // first load
    class Refreshing<T>extends ViewState<T> { T data; } // data on screen, poll in flight
    class Success<T>   extends ViewState<T> { T data; }
    class Partial<T>   extends ViewState<T> { T data; List<AppError> failures; }
    class Empty<T>     extends ViewState<T> {}
    class Failure<T>   extends ViewState<T> { AppError error; T? stale; }

`Partial` exists because the Dashboard and Operations screens aggregate several
independent endpoints, and a failing diagnostics call must never blank out the project
list. Every screen maps these seven states explicitly; a blank screen is a defect.

Controllers own: the state, the poll ticker, and cancellation. They are disposed by
GetX bindings on route exit, which cancels in-flight requests through the
`CancelToken` they hold.

As implemented in Phase 19.3, two details differ from what this section implied:

* The reload method is **`refreshData()`**, not `refresh()`. `GetxController`
  already defines `refresh()` to notify `GetBuilder` listeners, and shadowing it
  with an async reload is a trap for the next screen.
* Screen controllers are registered in the **route's** `binding`
  (`AppRoutes.pages`) rather than in `AppBindings`, so GetX disposes them on
  route exit. `AppBindings` holds only the long-lived objects: `ApiClient`,
  `ErrorMapper` and the six repositories.

A failed action is kept **separate** from the screen state: `ProjectsController`
exposes `actionError` alongside `state`, because a rejected Start must not blank
out a list that loaded correctly. See
[`flutter-ui-patterns.md`](flutter-ui-patterns.md) §5. `ProjectDetailController`
follows the same shape in Phase 19.4: `state` for the read, `isStarting` and
`actionError` for the write.

Two rules `ProjectDetailController` adds, both about not out-running the server:

* **`404` is not a `Failure` with stale data.** Every other error keeps the last
  known project on screen as `Failure(error, stale: project)`; a `404` publishes
  `Failure(NotFoundError())` with no stale, because a project that no longer
  exists must not keep rendering. The screen reads that as its own not-found
  state — with a route back to the list rather than a Retry, since retrying a
  `404` returns a `404`.
* **A mutation publishes only what the server returned.** `startProject` sets
  `Success(serverProject)` from the response body and writes no status of its
  own, before or after the call. A `409` or `404` triggers a re-read, because
  either one means the client's copy is stale.

`WorkItemsController` (Phase 19.5) reads two endpoints into one `state` rather
than one: it tries `getPlan` first, and only calls `listByProject` when that
comes back `null` (no plan yet) — never both, and never the plan alone, since
the plan carries no live status. A second, independent `Rx`,
`readinessState: Rx<ViewState<TaskReadiness>>`, tracks
`GET /api/tasks/{id}/readiness` for whichever item `selectedWorkItemId`
currently names; selecting a different item cancels the previous readiness
request rather than letting a slow response land on the wrong row.

The dependency edges themselves are validated, not just joined:
`validateDependencyGraph` (`presentation/models/dependency_graph_validation.dart`)
is a pure function over `ProjectPlan` that reports a self-dependency, a
duplicate dependency, a duplicate plan key or work item id, an edge to an
unknown plan key, and a cycle — without deleting any of the underlying nodes
or edges. `WorkItemDependencyTree` renders the resulting rows and defends its
own recursion with an ancestor set, so a cycle in the data is a dead end in
the tree rather than a stack overflow.

`AgentRunsController` (Phase 19.6) reads the one paged listing the API has,
`GET /api/agents/runs?projectId=...`, joined with `GET /api/agents/definitions`
for the agent name/type `AgentRunDto` does not itself carry — fetched once per
load, not once per row. `AgentRunsData.definitionFor(run)` is the lookup. A
status filter is a real server-side query parameter and re-fetches; a
work-item filter is client-side only, narrowing the already-loaded page,
because the API has no `workItemId` filter (gap `G-WORKITEM-EVENTS`) — the
same "no second-endpoint invention" discipline `WorkItemsController` applies
to `G-DEPS`. Selection is inline against the already-loaded page, exactly as
`WorkItemsController.selectedWorkItemId` works: the list endpoint already
returns each run's full `structuredResult`, so there is no separate detail
fetch and no per-row over-fetch (Workstream AH). `AgentRunDetailPanel` states
`model`/`provider`/fallback as "not exposed by the API" rather than omitting
the section or guessing (gaps `G-RUN-TELEMETRY`, `G-FALLBACK`) — the same
explicit-absence rule `WorkItemDetailPanel` already applies to acceptance
results. Lineage is Project → WorkItem → AgentRun only: the API has no way to
discover an `OrchestrationRun` id from a WorkItem or AgentRun (only from a
`POST` response or the ops-key-gated diagnostics summary), so no
Orchestration Run Detail screen was built this phase — see
[`flutter-roadmap.md`](flutter-roadmap.md) §19.6 for the reasoning.

`HumanReviewController` (Phase 19.7) owns exactly one thing: the approval
mutation. It fetches nothing of its own — `WorkItemsController` and
`AgentRunsController`, already bound to the same route, supply the review
evidence (Workstream AH) — and reads `ProjectDetailController.project` for the
one fact that actually gates approval: `Project.status == HumanReview`. That
check is a display gate only; the backend remains the sole authority, and
`approveProject()` re-checks it before calling `POST /api/projects/{id}/approve`
so the client never calls the endpoint for a project it can already see is not
eligible. Duplicate submission is guarded by the controller (`isApproving`),
not only by the disabled button. On success the controller calls
`ProjectDetailController.refreshData()` and sets `approvalSuccess` only after
that reload returns — the project's `Completed` status always comes from the
server response, never from a local assignment. A `409`/`404` sets
`approvalError` and reloads the authoritative project, the same "server
response wins" rule `ProjectDetailController.startProject` already applies;
a network/timeout/server error sets `approvalError` without reloading, since
the server state is unknown and reloading would blank evidence for no reason.

`OperationsController` (Phase 19.8) is the one controller that does **not**
keep a single `ViewState`. Operations is six independent endpoints, so it keeps
six — health, deployment, database, configuration, scheduler, agent metrics —
each with its own loading, failure and retry. `Future.wait` would abort the
whole set on the first error, so every section owns its try/catch and the
futures only ever complete normally; a `401` on metrics therefore leaves the
other five untouched. `DiagnosticsController` and `AuditController` stay
separate rather than folded in: diagnostics belongs to one project and is
reached by its own route, and the audit log pages independently.

**The operational credential** lives behind `OperationalCredentialStore`
(`lib/core/security/`), implemented over `flutter_secure_storage` — the
platform keystore, never `SharedPreferences`, never a file, never a Dart
constant. `OperatorAccessController` is its only holder: it reads the key and
hands it straight to `ApiClient.setOperationsKey`, which sets the request
header the backend defines. No widget, repository or other controller ever sees
the value — the UI observes `hasKey`, a boolean. The key is never interpolated
into an error message and never reaches a log, because `ApiHeaders.redacted`
already covers that header at the logging sink.

Whether a key is needed at all is the **backend's** decision: the API demands
one only when `Security:OperationalEndpoints:RequireAccessKey` is enabled,
which is off by default. The client therefore never demands one up front — it
reacts to a real `401` by re-reading the stored key and retrying **once**. A
second `401` is final and the operator is asked to act. The retry budget is one
per load pass rather than one per section, so a wrong key cannot fan out into
six retries or loop.

### Derived presentation models

Phase 19.4 introduces a third kind of object between the domain model and the
widget: a **pure presentation projection**, in `lib/presentation/models/`.
`ProjectLifecycle.of(project)` and `ProjectSourceView.of(project)` are
synchronous, take a domain model and return what the screen needs to draw —
no repository, no clock, no localization, no `Rx`. They exist so the rules that
matter most in this phase (which lifecycle stage may be called *current*; what
may be said about a source; which part of a repository URL may be displayed at
all) are unit-testable without pumping a widget.

One GetX constraint shapes how screens are written: an `Obx` tracks only the
observables read **while its closure runs**. A child widget's `build` executes
outside that scope, so values are either read in the closure and passed down as
parameters, or the subtree is built by plain functions called from inside it.
An `Obx` whose closure reads no observable throws at runtime.

---

## 8. Error model

    sealed class AppError {
      String get code;                 // stable, for logic and localization keys
      String? get correlationId;       // from X-Correlation-ID
      String? get technicalDetail;     // server detail, operator view only
    }

| `AppError` | Trigger |
|---|---|
| `NetworkError` | `DioExceptionType.connectionError`, DNS, socket, offline |
| `TimeoutError` | connect/send/receive timeout, HTTP `408`, `504` |
| `ValidationError` | HTTP `400`, `code: validation_error` |
| `NotFoundError` | HTTP `404`, `code: not_found` |
| `ConflictError` | HTTP `409` (`conflict`, `concurrency_conflict`) |
| `AuthenticationError` | HTTP `401` — a missing or wrong `X-Operations-Key` |
| `ForbiddenError` | HTTP `403`, `code: forbidden` |
| `RateLimitError` | HTTP `429` (`too_many_requests`) |
| `ProviderFailure` | HTTP `503` (`service_unavailable`, `database_unavailable`) |
| `ServerError` | HTTP `5xx` other than the above |
| `RequestCancelledError` | the caller aborted the request — expected, usually dropped silently |
| `UnknownError` | anything unmapped, including JSON decode failure |

Mapping is a single pure function `AppError mapError(DioException)`, unit-tested
against the exact `code` values listed in the API contract. It reads `code` first and
falls back to the HTTP status.

Presentation rules:

* Normal users see the **localized** message for `error.code` plus a retry action.
* Raw `detail`/`message` from the server, `correlationId`, endpoint and status code
  are shown only in the operator-facing diagnostics panel (collapsed by default).
* No stack traces are ever rendered.
* `correlationId` is always copyable — it is the join key into `/api/audit-log` and
  `/api/operations/{correlationId}`.

---

## 9. Localization (English + German)

Implemented in Phase 19.1. Full reference:
[`flutter-localization.md`](flutter-localization.md).

### 9.1 Decision

* **`easy_localization` 3.0.8** owns translations, runtime switching and the saved
  locale, with JSON assets under `assets/translations/`. The 4.x prerelease line
  is not used.
* `gen_l10n`/ARB is **not** used for application translations in this project.
* **GetX is not the localization engine.** It provides DI, state and navigation.
  Its one localization-adjacent role is that `GetMaterialApp` resolves
  `Get.locale ?? locale`, so `LocaleService` assigns `Get.locale` after a switch —
  otherwise the `MaterialApp` stays pinned to the language it was first built with.
* `intl` remains responsible for date, number and duration formatting
  (`core/utils/formatters.dart`).

### 9.2 Supported locales

`en` (fallback) and `de`. `localeResolutionCallback` behaviour is provided by the
package: an unsupported device language falls back to English. Neither locale is
RTL, so no directionality work was needed; widgets still use directional insets so
a future RTL locale costs nothing.

### 9.3 Preference and resolution

`LocaleService` persists a tri-state preference — System / English / Deutsch — which
the package itself has no concept of:

1. an explicit saved choice,
2. otherwise the device locale when supported,
3. otherwise English.

`apply()` calls `context.setLocale(...)` for an explicit choice, or
`deleteSaveLocale()` + `resetLocale()` for System default. Switching is immediate,
with no restart.

### 9.4 String and enum rules

* No user-visible literal string in a widget; everything goes through `'key'.tr()`.
* Semantic dotted keys (`nav.projects`, `enums.projectStatus.humanReview`), never an
  English sentence as a key.
* Backend wire values are identifiers and are never translated; `EnumLabels` maps a
  parsed enum to a translation key, and an unparsable value renders as
  `Unknown (RawValue)`.
* Operator data (project names, work item titles, ids, model names) is never
  translated; server `detail` text stays English in the operator diagnostics block.

### 9.5 Rebuild mechanics

`.tr()` reads a static table, so nothing depends on the locale unless it asks.
`AppShell` reads `context.locale` to register that dependency and takes its page
content as a `WidgetBuilder`, so the page below it is rebuilt too. Any future shell
or page must follow the same pattern.

### 9.6 Layout

German strings run 20–35 % longer than English. No layout is sized to English text;
the extended rail reserves 208 dp, and every breakpoint is verified with German
strings in the widget tests.

---

## 10. Testing strategy

| Level | Scope | Tooling |
|---|---|---|
| Unit — mappers | DTO → domain for every endpoint, using captured real payload fixtures | `flutter_test` |
| Unit — enums | Round-trip and `unknown` fallback for every enum | `flutter_test` |
| Unit — errors | `mapError` for each documented `code` and status | `flutter_test` |
| Unit — l10n | `en.json`/`de.json` key parity, enum label coverage, locale resolution and persistence | `flutter_test` |
| Repository | Endpoint, query params, headers, retries, cancellation | `http_mock_adapter` |
| ViewModel | All seven `ViewState` transitions, poll start/stop, cancellation | `flutter_test` + hand-written repository fakes |
| Widget | Loading / empty / error / partial / success per screen, plus each screen at compact / medium / expanded width in both languages | `flutter_test` |
| Integration | The operator flow in the **real** application against a **live** API, on a real target | `integration_test` |
| Golden | 96 pixel goldens of the real screens: all ten × `en`/`de` × compact/medium/expanded in light, six critical screens again in dark (Phase 19.10) | `flutter_test` goldens, `--tags golden` |
| Accessibility | Semantic labels, keyboard operation and focus order, contrast (computed WCAG pairs + `textContrastGuideline`), 100–200 % text in both languages, reduced motion | `flutter_test`, `test/accessibility/` |
| Contract | Committed fixtures captured from a throwaway API and normalized; a fresh capture is compared with them and any contract drift fails (`tool/api_fixtures.dart --check`) | Dart script + a locally started API |
| Security | Secret **values** (not references) absent from sources, tests, fixtures, assets and builds; the operational key only in `flutter_secure_storage` (memory only on the Web); release builds without a development address | `tool/secret_scan.dart`, `test/security/` |
| Performance | Large lists (5 000 projects, 1 000 work items / runs), dependency trees up to 20 000 nodes, and "an unchanged poll rebuilds nothing" | `test/performance/` |
| Soak | Six simulated hours per live screen with latency, failures and a model of the backend rate limiter; lifecycle and open/leave churn; plus a live run against the real API | `fake_async`, `integration_test/soak_rate_limit_test.dart` |

The full matrix — every category with the files that implement it, the golden matrix,
the fixture workflow, the soak strategy and the CI gates — is
[`flutter-testing.md`](flutter-testing.md).

The contract fixtures are the defence against silent backend drift: they are captured
from a running API on a throwaway database, normalized (stable ids and instants,
timestamp formats preserved), committed, and re-checked against a fresh capture in CI.

ViewModel tests use hand-written fakes (`test/support/fake_repositories.dart`) rather
than a mocking package: they record every call, which is what lets the
duplicate-submission tests assert that one press produced exactly one request, and
they expose a gate so "the button is disabled while pending" is observable.

### Why there is an `integration_test/` directory

A widget test cannot exercise a real API, and the reason is worth recording so the
next phase does not rediscover it: `TestWidgetsFlutterBinding` installs an
`HttpOverrides` that answers every request with `400` and opens no socket, and its
fake-async zone fires Dio's timeout timers under a request that is still in flight
for real. Both are properties of the binding, not of the client.

`IntegrationTestWidgetsFlutterBinding` runs the actual application on a real target
with real networking and a real clock. Added in Phase 19.3:

| Test | Scenario |
|---|---|
| `integration_test/operator_flow_test.dart` | Dashboard → Projects → create → source → open the detail placeholder → back → start → refresh → English/German/English. Requires a live API on an **empty** database |
| `integration_test/offline_flow_test.dart` | The API unreachable: error state, Retry, a still-navigable shell, and Settings still working. Requires a base URL nothing is listening on |

```bash
flutter test integration_test/operator_flow_test.dart -d windows --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
```

Neither runs under a plain `flutter test`, which only collects `test/`, so the default
suite stays hermetic.

---

## 11. Configuration and security

`AppConfig` holds: `baseUrl`, `operationsKey` (optional), `pollIntervals`, `locale`,
`themeMode`. It is loaded from a local settings store, editable in Settings.

Hard rules, enforced by review, by `test/security/` and by the CI secret scan of `lib/`, `test/`, `integration_test/`, `assets/` and the release build output:

* The client holds **no provider credentials**: no OpenRouter key, no Hermes
  credential, no model-provider token, ever — not in source, not in config, not in
  build arguments.
* The client never calls OpenRouter, Hermes, or any provider directly. It talks only to
  the AI Factory API.
* The client never touches SQLite, the filesystem workspace, or any internal path.
* The only secret the client may hold is the operator's `X-Operations-Key`, entered by
  the operator in Settings and stored with `flutter_secure_storage`. It is never logged,
  never shown after entry (masked), and never placed in a URL or query string.
* The client never sets `ProjectStatus.Completed`. Completion goes through
  `POST /api/projects/{id}/approve` and nothing else.
* **Web builds keep the key in memory only** (Phase 19.10):
  `flutter_secure_storage` on the web is `localStorage` encrypted with a key that sits
  beside it, which is persistence without protection. The operator is told the key
  lasts for the session (`G-OPS-KEY-UX`).
* **A release build never defaults to the development API.** Without
  `AI_FACTORY_ENV`, a debug build is `development` (local API) and a release build is
  `production`, which has no default address: it must be built with
  `--dart-define=AI_FACTORY_API_BASE_URL=…` and otherwise reports itself unconfigured.
  Request logging is on in `development` only, and redacts the key even there.

    Flutter -> AI Factory API -> Provider / Hermes / SQLite     (the only shape)
    Flutter -> OpenRouter                                        (never)

---

## 12. Live updates

*Implemented in Phase 19.9.* The backend exposes no SSE, WebSocket, SignalR or
long-polling endpoint (gap `G-REALTIME`), so the client uses **controlled polling**.
It is not realtime: an update is seen within one interval, not the moment it
happens, and nothing is pushed.

    Page ──LiveUpdatesScope──▶ Controller ──▶ PollingCoordinator ──▶ Repository ──▶ ApiClient
         (visibility only)     (owns state)   (owns *when*)

### 12.1 `PollingCoordinator` (`lib/core/polling/`)

One per live screen, owned by that screen's existing controller (`live`). It
decides **when** a cycle runs; the controller's task decides **what** is read and
replaces the authoritative state. No widget owns a timer.

| Guarantee | How |
|---|---|
| One timer | `start()` is idempotent; the single `Timer` is re-armed only after a cycle completes |
| No overlap | A cycle never starts while one is in flight. `refreshNow()` during a cycle queues **one** follow-up (a manual trigger outranks a background one); every caller's future completes when it has run |
| Stop | `stop()` cancels the timer; a cycle in flight finishes but schedules nothing |
| Dispose | `dispose()` cancels the timer and the lifecycle subscription, completes queued callers, and publishes nothing afterwards. Controllers call it in `onClose` |
| Deterministic tests | Time comes from `Timer` plus an injected clock; tests use `fake_async` / the widget tester's clock and never sleep |

Every read goes through the coordinator — first load, manual refresh, retry and
the refresh after a mutation — so none of them can overlap a poll. Background
cycles are **silent**: the state is not flipped to `Refreshing`, so content never
flickers or blanks; the result simply replaces it.

### 12.2 Intervals (`PollIntervals`, constructor-injected)

| Screen | Interval | Adaptive rule |
|---|---|---|
| Dashboard | 10 s | 30 s (`idle`) when no project is active |
| Projects | 10 s | 30 s when no project is active |
| Project Detail | 5 s | 3 s while an agent run is queued/running/waiting; 5 s while the project is advancing or a work item is executing; 5 s in `HumanReview` (so another operator's approval is seen); 30 s for `Created`/`Paused`; **none** for `Completed`/`Failed`/`Cancelled` |
| Project Diagnostics | 10 s | 30 s while waiting; **none** once terminal |
| Operations | 15 s | Only health, scheduler activity and agent metrics; deployment, database and configuration are read on open and on manual refresh only |
| Settings, audit log | — | Not polled |

The interval is re-evaluated after every cycle from what the server last reported.
A manual refresh still works on a stopped screen and resumes polling if the state
has become non-terminal.

### 12.3 Failure policy (`PollOutcome`, `PollBackoff`)

| Outcome | Errors | Behaviour |
|---|---|---|
| Transient | Network, timeout, 5xx, 503, unknown | Keep data; back off ×2 per failure up to 60 s; reset on the first success |
| Rate limited | 429 | Straight to the 60 s ceiling |
| Unauthorized | 401 | **Stop** ticking; "Access required". Resumed only by an explicit refresh — the operator saving a key — never by a timer, a screen return or an app resume |
| Permanent | 400, 403, 404, 409, 422 | **Stop** ticking; "Refresh failed". A deleted project cannot cause an endless loop |
| Cancelled | superseded | Neutral |

On a multi-source screen the *primary* source decides permanence (the project on
Project Detail); a permanent failure in a secondary section is shown by that
section and does not stop the rest of the screen. Operations skips a section that
failed permanently until the next manual refresh.

### 12.4 Visibility and application lifecycle

* **Screen:** `LiveUpdatesScope` starts the coordinator on mount and stops it on
  unmount. A `RouteObserver<PageRoute>` (`liveRouteObserver`, registered on the
  navigator) pauses it while another page covers it and refreshes immediately when
  uncovered. Dialogs are not pages, so a confirmation dialog does not pause.
  Shell navigation uses `Get.offNamed`, which disposes the previous screen's
  controller outright.
* **Application:** `AppActivity` wraps `AppLifecycleListener`. `hidden`, `paused`
  and `detached` pause; `resumed` refreshes immediately. `inactive` does **not**
  pause: on desktop it means the window merely lost focus while still visible.
* Both pauses are independent; polling resumes only when neither applies.

### 12.5 Coordinated refresh on Project Detail

`ProjectDetailController` is given `WorkItemsController` and `AgentRunsController`
by the route binding and runs **one** cycle for all three: project, plan (or task
list), agent runs. A section still on its own first load is not read twice.
`refreshData()` (refresh button, after Start, after approval) runs the same cycle.
Agent definitions are configuration and are re-read only on a manual refresh.

### 12.6 Request budget

Only the visible screen polls. Worst cases, requests per minute:

| Screen | Per cycle | Rate |
|---|---|---|
| Project Detail, agent run executing (3 s) | 3 (project, plan, runs) — 4 when no plan exists | 60–80 |
| Project Detail, advancing (5 s) | 3–4 | 36–48 |
| Dashboard (10 s) | 5 (projects, live, ready, scheduler, metrics) | 30 |
| Operations (15 s) | 4 (live, ready, scheduler, metrics) | 16 |
| Diagnostics (10 s) | 1 (`OperationalApi`) | 6 |

All within the `GeneralApi` 100 req/min and `OperationalApi` 50 req/min budgets;
a `429` backs off to 60 s regardless. Both limiters are **global** on the backend
(one fixed-window partition for every caller), so the budget is shared with every
other client of the same API.

### 12.7 Soak strategy and unchanged polls (Phase 19.10)

Two layers of evidence, kept apart:

* **Deterministic** (`test/soak/polling_soak_test.dart`): six simulated hours per live
  screen under `fake_async`, with latency, a seeded failure schedule, a ten-minute
  outage and a model of the backend's fixed-window limiter; 500 background/foreground
  switches; 200 open → poll → leave cycles per screen. It records polls, successes,
  failures, backoff events, the busiest minute per policy, overlap, pending timers
  and live coordinators (`PollingCoordinator.debugLiveCount`).
* **Live** (`integration_test/soak_rate_limit_test.dart`): the real application
  against a real API on a throwaway database, walking every live screen repeatedly
  with the navigation patterns that could duplicate polling, metering every request
  and every `429`.

`ViewState` has value equality, and GetX does not notify for an equal value: a poll
that returns exactly what is on screen rebuilds nothing, however long the list
(`test/performance/large_list_performance_test.dart`, "polling does not rebuild").

When a realtime endpoint is added (gap `G-REALTIME`), the coordinator's task can be
fed by it and the screens are unaffected.
