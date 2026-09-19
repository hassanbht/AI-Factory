# Flutter Client — API Client, DTOs, Domain Models and Repositories

Phase 19.2 · Application: `apps/ai_factory_client` · Backend: AI Factory `v1.0.0`
Companion documents: [`flutter-api-contract.md`](flutter-api-contract.md),
[`flutter-architecture.md`](flutter-architecture.md),
[`flutter-api-gaps.md`](flutter-api-gaps.md),
[`flutter-localization.md`](flutter-localization.md).

Two rules hold throughout:

    Widgets never call Dio directly.
    API DTOs never reach presentation widgets.

---

## 1. The chain

    Widget / Controller
        -> Repository interface      (domain/repositories/repositories.dart)
        -> Repository implementation (data/repositories/*.dart)
        -> ApiClient                 (core/network/api_client.dart)
        -> Dio                       (+ interceptors)
        -> AI Factory REST API
        -> JSON
        -> DTO                       (data/dto/*.dart, json_serializable)
        -> Domain model              (data/mappers/mappers.dart)

A `DioException` never escapes `ApiClient`, and a DTO never escapes a
repository. Everything above the data layer sees domain models and `AppError`.

## 2. Configuration

`AppConfig.fromEnvironment()` reads compile-time defines:

```bash
flutter run -d windows --dart-define=AI_FACTORY_ENV=development --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
```

| Key | Meaning |
|---|---|
| `AI_FACTORY_ENV` | `development` (default) or `production` |
| `AI_FACTORY_API_BASE_URL` | Base URL; defaults to `http://localhost:5223` in development and is **empty** in production so no private address is committed |

Timeouts: connect 10 s, send 30 s, receive 30 s.

## 3. ApiClient

One Dio instance for the whole application, created once in `AppBindings`.
Repositories never construct their own.

Responsibilities: base URL, timeouts, `Accept` header, the optional
operational key, correlation ids, safe logging, cancellation, and translating
every failure into an `AppError`.

Typed helpers — `getJson`, `getJsonList`, `getText`, `postJson`, `patchJson` —
mean a repository never casts `dynamic`. A response whose shape changed fails
in the data layer with a named error instead of somewhere inside a mapper.
`getText` exists because `/health/*` answers `text/plain`, not JSON.

`setOperationsKey(String?)` sets or clears `X-Operations-Key`. It is a secret:
never logged, never placed in a URL or query string. The key itself is entered
by the operator and stored securely from Phase 19.8 onwards.

### Interceptors

| Interceptor | Behaviour |
|---|---|
| `CorrelationInterceptor` | Attaches a UUID v4 `X-Correlation-ID` to every request, so the operator can paste it into `/api/operations/{correlationId}` |
| `SafeLoggingInterceptor` | Development only. Logs method, URL, status, duration and correlation id. Header values in `ApiHeaders.redacted` (`authorization`, `x-operations-key`, cookies, proxy auth) render as `***`, and **request and response bodies are never logged** |

## 4. Error mapping

`ErrorMapper` is a pure function from a transport failure to an `AppError`. It
reads the backend's stable `code` extension **first** and falls back to the
HTTP status, because the status alone is ambiguous: `409` covers both a
conflict and a concurrency clash, `503` both an open circuit and a busy
database.

| Backend `code` / condition | `AppError` | Retryable |
|---|---|---|
| `not_found`, 404 | `NotFoundError` | no |
| `validation_error`, 400 | `ValidationError` | no |
| `conflict`, `concurrency_conflict`, 409 | `ConflictError` | no |
| 401 | `AuthenticationError` (missing/wrong operational key) | no |
| `forbidden`, 403 | `ForbiddenError` | no |
| `timeout`, 408, 504, Dio timeouts | `TimeoutError` | yes |
| `too_many_requests`, 429 | `RateLimitError` | yes |
| `service_unavailable`, `database_unavailable`, 502/503 | `ProviderFailure` | yes |
| `database_error`, `internal_error`, other 5xx | `ServerError` | no |
| connection error, bad certificate | `NetworkError` | yes |
| caller cancelled | `RequestCancelledError` | n/a |
| anything else, including a malformed body | `UnknownError` | no |

Every `AppError` carries `messageKey` (a translation key — the UI never shows
raw server text to a normal user), plus `technicalDetail`, `statusCode`,
`backendCode` and `correlationId` for the operator diagnostics block.

HTTP 422 is listed in the phase brief but the API never emits it; no mapping
was invented for it. It falls through to `UnknownError` like any other
unexpected status.

## 5. DTO conventions

* One file per area under `data/dto/`, generated with `json_serializable`
  (`dart run build_runner build`). Generated `*.g.dart` files are committed.
* A DTO models **exactly** what the endpoint returns. A field the backend
  persists but does not expose — `provider`, `model`, `maxAttempts`,
  `acceptanceCriteria`, `dependsOn` on a work item — is absent, not nullable:
  a nullable field would be indistinguishable from "the backend said none".
* Enums stay as raw strings in the DTO. Parsing belongs to the mapper.
* Requests (`data/dto/requests.dart`) are hand-written, because omitting a
  null optional matters: `POST .../approve` accepts `{}`, which is not the
  same request as `{"actor": null}`.

### Two serialization quirks of this backend, both verified live

**Timestamps are inconsistent.** The same value is returned with a `Z` on a
write path and without one on a read path:

    POST /api/projects -> "createdAtUtc":"2026-09-19T19:02:11.4453985Z"
    GET  /api/projects -> "createdAtUtc":"2026-09-19T19:02:11.4453985"

The in-memory `DateTime` has `Kind=Utc`; SQLite restores it as
`Kind=Unspecified`. `DateTime.parse` would read the second form as **local**
time and silently shift every read-back timestamp by the operator's UTC
offset. `UtcDateTimeConverter` always produces UTC.

**Enums in raw record responses are numbers.** The API configures no
`JsonStringEnumConverter`, so endpoints that return a C# record directly
serialize enum members as ordinals — confirmed against the live API:

    "eventType":31,"severity":1

Endpoints that stringify explicitly (`Status = ...ToString()`) send names, as
documented per endpoint in `flutter-api-contract.md`. Project diagnostics is
the affected surface in this phase: `lastFailureCategory` and
`latestApprovalDecision` are ordinals. The mapper accepts **either** form, and
the enums expose `fromOrdinal` alongside `fromWire`. Declaration order in
`backend_enums.dart` therefore has to match the backend enums exactly; an
out-of-range ordinal degrades to `unknown` rather than throwing.

A `TimeSpan` (`/api/operations/deployment` `uptime`) arrives as
`"00:00:00.0002757"`, which Dart cannot parse natively; `TimeSpanConverter`
handles it.

## 6. Domain models and enum parsing

Domain models are immutable, `Equatable`, free of JSON and free of `Rx`.

Every parsed enum is stored alongside its raw wire value (`status` +
`rawStatus`). `fromWire` is case-insensitive and never throws; an unrecognized
value becomes `unknown`, and the UI renders `Unknown (RawValue)` so a state a
newer backend introduced stays visible instead of turning into a blank.
Display text remains the job of `EnumLabels`, never of a DTO or mapper.

`WorkItemNode` / `ProjectPlan` exist because the dependency graph is split
across two endpoints (gap `G-DEPS`): `GET .../plan` has the edges,
`GET .../tasks` has live state. The join lives in `mapProjectPlan`, and a plan
entry whose work item is missing is skipped rather than rendered from plan
data alone.

## 7. Repositories

| Interface | Implementation | Endpoints |
|---|---|---|
| `ProjectRepository` | `ApiProjectRepository` | list, get, create, source, start, status, plan |
| `WorkItemRepository` | `ApiWorkItemRepository` | list by project, get, readiness, status |
| `AgentRunRepository` | `ApiAgentRunRepository` | list (paged), get, create, cancel, definitions |
| `DiagnosticsRepository` | `ApiDiagnosticsRepository` | project diagnostics, agent metrics |
| `OperationsRepository` | `ApiOperationsRepository` | health, deployment, database, configuration, scheduler |
| `ApprovalRepository` | `ApiApprovalRepository` | approve |

Notable behaviours:

* `getPlan` returns **null** for a `404`: "this project has no plan yet" is a
  legitimate state, not an error.
* `getHealth` turns a `503` readiness answer into `isReady: false` — the
  caller asked whether it is healthy, and "no" is a valid reply — while an
  unreachable `/health/live` reports `SystemHealth.unreachable` instead of
  guessing.
* `cancelRun` returns after a `202`: cancellation is *requested*, not
  completed, so callers re-read rather than assume.
* `ApprovalRepository` is the only completion path. It never writes
  `Completed` through the status endpoint (the backend rejects that by
  design), never records an approval locally, and surfaces the `409` from a
  wrong state rather than working around it.

**No caching.** A screen asks a repository, a repository asks the API. The
only exception is the 2 s request-collapsing cache described in
`flutter-architecture.md` §6, which is not implemented yet and will arrive
with the polling phase if it proves necessary. There is no Hive, Isar or
SQLite in this client.

## 8. Dependency injection

`AppBindings` registers `ApiClient` and all six repositories with
`Get.lazyPut(fenix: true)`, **keyed by interface**. A controller resolves
`Get.find<ProjectRepository>()` and never learns which implementation it got.

A gotcha worth knowing, pinned by `test/app/repository_substitution_test.dart`:
a `fenix` builder survives `Get.deleteAll(force: true)`, and `Get.lazyPut`
refuses to overwrite a live registration. Once `AppBindings` has run in a test
process, `put`, `replace` and `lazyReplace` on those types are all defeated by
the surviving builder. A test that needs a fake therefore registers it
**before** the bindings run, in a process where they have not run yet.

## 9. Cancellation

`RequestCancellation` (in `core/network/`) wraps a Dio `CancelToken` so the
repository interfaces stay free of a transport type. Every repository method
accepts an optional one.

    final cancellation = RequestCancellation();
    final future = repository.list(cancellation: cancellation);
    // on dispose, or when a newer poll supersedes this one:
    cancellation.cancel();

A cancelled request completes with `RequestCancelledError`, which a controller
normally drops silently. It is single-use, like the token it wraps, and this
is the same mechanism the polling phase will use.

## 10. Testing

| Test | Covers |
|---|---|
| `test/data/dto_serialization_test.dart` | Every DTO against **recorded** payloads; round-trip; nulls, empty collections, nested objects, missing arrays; request body shapes; both JSON converters |
| `test/data/mappers_test.dart` | DTO → domain for every area; unknown enum degradation with the raw value preserved; numeric-enum resolution; the plan/work-item join |
| `test/data/project_repository_test.dart` | The integration boundary: repository → ApiClient → Dio → mocked HTTP → DTO → domain, plus every error mapping and cancellation |
| `test/data/repositories_test.dart` | The other five repositories, including the 401 path and the health-probe semantics |
| `test/app/bindings_test.dart` | Resolution by interface, one shared `ApiClient`, config wiring, ops key as a header |
| `test/app/repository_substitution_test.dart` | The fake-substitution recipe |
| `test/data/live_api_smoke_test.dart` | Opt-in end-to-end run against a real API |

Fixtures under `test/fixtures/api/` are **recordings**, captured by calling a
running `v1.0.0` API — not hand-written examples. Regenerating them after a
backend release is how contract drift gets caught.

The live smoke test is skipped unless a base URL is supplied:

```bash
flutter test test/data/live_api_smoke_test.dart --dart-define=AI_FACTORY_SMOKE_BASE_URL=http://localhost:5223
```

It needs no provider credential of any kind: the client talks only to the AI
Factory API.

## 11. Security

* No provider credentials anywhere in the client: no OpenRouter key, no Hermes
  credential, no model-provider token.
* The client never calls OpenRouter, Hermes or a database directly.
* The only secret it may hold is the operator's `X-Operations-Key`: header
  only, redacted in logs, never in a URL.
* The configuration endpoint returns flags and names only; a test asserts the
  recorded snapshot contains no connection string or key field.
* No response body is ever logged.
