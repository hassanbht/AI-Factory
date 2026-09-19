# Flutter Client — Architecture Specification

Target backend: AI Factory `v1.0.0` · Application: `apps/ai_factory_client`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md), [`flutter-ux-spec.md`](flutter-ux-spec.md),
[`flutter-localization.md`](flutter-localization.md),
[`flutter-roadmap.md`](flutter-roadmap.md).

Written in Phase 19.0 as a specification; sections 3, 4 and 9 were updated in
Phase 19.1, and sections 4, 5, 6, 8 and 11 in Phase 19.2, to describe what was
actually built. The implemented data layer is documented in detail in
[`flutter-api-client.md`](flutter-api-client.md). Sections covering ViewModels
and polling remain forward-looking until the phase that implements them.

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

As built in Phase 19.1 (`apps/ai_factory_client`):

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
        pages/                      # dashboard, projects, operations, settings
        widgets/                    # state views, language selector, placeholder
    assets/translations/{en,de}.json
    test/{core,localization,widget,support}/

`core/network/request_cancellation.dart` carries the cancellation handle that
keeps the repository interfaces free of a Dio type.



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
| ViewModel | All seven `ViewState` transitions, poll start/stop, cancellation | `mocktail` |
| Widget | Loading / empty / error / partial / success per screen | `flutter_test` |
| Golden | Desktop + tablet × `en` + `de` for Dashboard, Project Detail, Human Review — deferred to Phase 19.10, where the screens exist | `flutter_test` goldens |
| Contract | Fixtures regenerated from the live API in CI; a drift fails the build | script + `dotnet run` API |

The contract fixtures are the defence against silent backend drift: they are captured
from a running `v1.0.0` API, committed, and diffed on every backend release.

---

## 11. Configuration and security

`AppConfig` holds: `baseUrl`, `operationsKey` (optional), `pollIntervals`, `locale`,
`themeMode`. It is loaded from a local settings store, editable in Settings.

Hard rules, enforced by review and by a CI secret scan of `lib/`:

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

    Flutter -> AI Factory API -> Provider / Hermes / SQLite     (the only shape)
    Flutter -> OpenRouter                                        (never)

---

## 12. Live updates

The backend exposes no SSE, WebSocket, SignalR or long-polling endpoint (verified by
source inspection). The client therefore polls, under a shared `PollingController` with:

| Property | Rule |
|---|---|
| Base interval | Dashboard 15 s · Project Detail (active) 5 s · AgentRun detail (active) 5 s · Operations 30 s |
| Terminal states | Polling **stops** on `Completed`, `Failed`, `Cancelled` (project), and on terminal AgentRun/orchestration statuses |
| Inactive projects | `Created`, `Paused`, `HumanReview` → 30 s (nothing is advancing on its own) |
| Backoff | On error: ×2 up to 60 s; reset to base on the first success |
| `429` | Immediately back off to 60 s and surface a "slow mode" indicator |
| Lifecycle | Suspended when the window is hidden/minimized (`AppLifecycleState.paused`), resumed with an immediate fetch |
| Navigation | Cancelled on route exit via the controller's `CancelToken` |
| Overlap | A tick is skipped while the previous request is still in flight |

Each poll is a single request per visible pane; a screen never fans out to more than
four endpoints per cycle. This keeps the whole client well inside the `GeneralApi`
100 req/min and `OperationalApi` 50 req/min budgets with room for operator actions.

When a realtime endpoint is added (gap `G-REALTIME`), only `core/polling/` and the
repository subscription methods change; ViewModels and widgets are unaffected.
