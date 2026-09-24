# Flutter client — testing

How the AI Factory Flutter client (`apps/ai_factory_client`) is tested, what each
suite proves, how to run it, and what CI runs. Established in Phase 19.10.

Two kinds of evidence are kept strictly apart:

* **Deterministic** — widget, controller, repository and soak tests with fakes and
  fake time. Hermetic, run by every `flutter test`, and in CI.
* **Real backend** — `integration_test/` on native Windows against a real AI Factory
  API started on a **throwaway** database. Opt-in; never against
  `C:\AI-Factory\data\factory.db`.

A behaviour proven only by the first kind is never reported as proven by the second.

---

## 1. Test matrix

Every category below has executable tests; none relies on a description.

| Category | Files |
|---|---|
| Unit | `test/core/error_mapper_test.dart`, `test/core/polling_coordinator_test.dart`, `test/presentation/project_lifecycle_test.dart`, `test/presentation/project_list_filter_test.dart`, `test/presentation/dependency_graph_validation_test.dart`, `test/localization/relative_time_test.dart` |
| Widget | `test/widget/*_test.dart` (13 files) |
| Controller | `test/presentation/*_controller_test.dart` (12 files), `test/presentation/live_updates_controllers_test.dart` |
| Repository | `test/data/project_repository_test.dart`, `test/data/repositories_test.dart`, `test/app/repository_substitution_test.dart`, `test/app/bindings_test.dart` |
| Serialization | `test/data/dto_serialization_test.dart`, `test/data/mappers_test.dart`, `test/contract/fixture_contract_test.dart` |
| Localization | `test/localization/translation_coverage_test.dart`, `translation_files_test.dart`, `enum_labels_test.dart`, `locale_service_test.dart` |
| Responsive | `test/widget/responsive_shell_test.dart`, every page test at compact/medium/expanded, `test/accessibility/text_scaling_test.dart`, the golden matrix |
| Accessibility | `test/accessibility/semantics_test.dart`, `keyboard_navigation_test.dart`, `contrast_test.dart`, `text_scaling_test.dart`, `reduced_motion_test.dart` |
| Golden | `test/golden/screens_golden_test.dart` (96 goldens, tag `golden`) |
| Polling | `test/core/polling_coordinator_test.dart`, `test/presentation/live_updates_controllers_test.dart`, `test/widget/live_updates_test.dart`, `test/soak/polling_soak_test.dart` |
| Security | `test/security/secret_scan_test.dart`, `credential_storage_test.dart`, `release_config_test.dart`, `test/presentation/operator_access_controller_test.dart` |
| Error handling | `test/regression/error_matrix_test.dart`, `test/core/error_mapper_test.dart` |
| Performance | `test/performance/large_list_performance_test.dart`, `dependency_tree_performance_test.dart` |
| Navigation / state restoration | `test/regression/navigation_regression_test.dart`, `test/widget/project_navigation_test.dart` |
| Integration (real backend) | `integration_test/real_backend_lifecycle_test.dart`, `real_backend_operations_test.dart`, `real_backend_diagnostics_test.dart`, `soak_rate_limit_test.dart`, and the earlier flows in §8 |

```bash
flutter test                              # everything under test/, goldens included
flutter test --exclude-tags golden        # without goldens
flutter test --tags golden                # goldens only
```

---

## 2. Goldens

`test/golden/screens_golden_test.dart` renders the real screens through their
production routes with one deterministic data set, `test/support/screen_catalog.dart`.

| Theme | Screens | Locales × sizes | Goldens |
|---|---|---|---|
| Light | Dashboard, Projects, Create Project, Project Detail, WorkItems, AgentRuns, Human Review, Operations, Project Diagnostics, Settings | `en`, `de` × compact 400, medium 800, expanded 1280 | 60 |
| Dark | Dashboard, Project Detail, WorkItems, AgentRuns, Human Review, Operations | same | 36 |

WorkItems, AgentRuns and Human Review are sections of Project Detail; their goldens are
the detail screen with a work item / an agent run selected, or the review project,
scrolled to that section. Long screens are captured on a taller window (2400 / 2000 /
1600 px) so every section is in the image.

**Determinism.** Pinned: the clock and the display time zone (`AppClock.pin`, UTC),
locale, theme, text scale 1.0, device pixel ratio 1.0, window size, every id and
timestamp (fixed catalog data dated relative to the pinned "now"), network (fakes). No
randomness is involved. Real Roboto and Material Icons are loaded from the Flutter SDK
cache (`bin/cache/artifacts/material_fonts`) so the goldens are reviewable.

**Platform.** The goldens are rendered by the **Windows** test engine and compared
pixel for pixel (no tolerance). CI runs them on `windows-latest`. Regenerate only after
an intentional UI change, on Windows, and review the images:

```bash
flutter test --update-goldens test/golden
```

On a mismatch, `test/golden/failures/` (git-ignored, uploaded by CI) holds the diffs.

---

## 3. Accessibility

| Area | How it is verified |
|---|---|
| Semantic labels | `semantics_test.dart`: lifecycle stage and position, work item, dependency row, agent run, approval consequence, live status (live region), diagnostics counts with their group, health probes, errors with Retry, and every status chip of every enum as "<field>: <state>" with an icon |
| Keyboard | `keyboard_navigation_test.dart` at a real 1280×900 window: Dashboard → Projects → Project Detail → WorkItem → AgentRun → back → Operations with Tab/Shift+Tab/Enter/Space only; arrow keys in the agent-run status filter; the approval dialog (default focus Cancel, Tab/Shift+Tab, Escape cancels and returns focus, Space approves exactly once); Escape closes Create Project |
| Focus order | Project Detail: Back → Diagnostics → Refresh → work items → dependency rows → agent-run refresh → status filter → agent runs → Approve |
| Focus visibility | 2 dp primary outline on focused buttons (theme) and custom tiles (`FocusRing`); ≥ 3:1 against every surface (`contrast_test.dart`) |
| Contrast | `contrast_test.dart`: computed WCAG ratios, light and dark, for body/secondary text, every status tone on its tint over every surface, errors, warnings, selected rows, the navigation indicator, filled buttons, the focus ring; plus Flutter's `textContrastGuideline` over eight rendered screens in both themes. Disabled controls are exempt (WCAG 1.4.3) |
| Text scaling | `text_scaling_test.dart`: all ten screens × 1.0 / 1.25 / 1.5 / 2.0 × `en`/`de` × compact/expanded (160 renderings, real font metrics); any overflow fails. At 200 % the Approve button and both dialog actions are reachable and on screen |
| Reduced motion | `reduced_motion_test.dart`: with `disableAnimations`, no route transition, no dialog fade, a still icon instead of the live-status spinner — and "Refreshing…" is still announced |

---

## 4. Localization integrity

`translation_coverage_test.dart` scans all of `lib/` and fails on: a key used but
missing in either language; an **orphan** key defined but never used (enum and filter
keys built at runtime are enumerated from their Dart enums); a plural key called with
`.tr()` or a plain key with `.plural()`; a plural key without `one` and `other` in both
languages; placeholder mismatches between English and German (positional count and
named set); unbalanced braces or a value that is itself a key; a call site whose `args`
count differs from its placeholders; a hard-coded user-visible literal in `Text`,
`SelectableText`, `label`, `tooltip`, `hintText`, `labelText`, `helperText`,
`semanticsLabel`, `message` or `title` (only the product name "AI Factory" is allowed).

---

## 5. API contract fixtures

### 5.1 Regeneration

`tool/api_fixtures.dart` turns real API responses into committed fixtures:

    real API response → normalized fixture → test/fixtures/api/*.json

1. Start the API on a **new** throwaway SQLite file, in `Development`, with migrations:

   ```powershell
   $db = "$env:TEMP\ai-factory-fixtures-$(Get-Date -Format yyyyMMddHHmmss).db"
   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   $env:ASPNETCORE_URLS = 'http://127.0.0.1:5299'
   $env:Database__ConnectionString = "Data Source=$db"
   $env:ConnectionStrings__FactoryDatabase = "Data Source=$db"
   $env:Database__AutoMigrate = 'true'
   dotnet apps/src/AI.Factory.Api/bin/Debug/net10.0/AI.Factory.Api.dll
   ```

   The API log proves the database path (`SqliteDataDirectoryReady … DatabaseFile=…
   Exists=False`) and the applied migrations. Never point it at
   `C:\AI-Factory\data\factory.db`.

2. From `apps/ai_factory_client`:

   ```bash
   dart run tool/api_fixtures.dart --base-url http://127.0.0.1:5299 --write
   ```

The tool **refuses** a non-loopback URL (unless `--allow-remote` for a dedicated test
environment), an API reporting `Production`, and a database that already holds
projects — so no real project data can reach a fixture. It stores no credential; if the
test API requires an operational key, it is read from
`AI_FACTORY_FIXTURE_OPERATIONS_KEY`, sent as a header and never written. Every capture
passes the secret scanner before anything is written.

The scenario is deterministic: a project taken to `Planning` with two agent
definitions, a two-task plan and a queued agent run; a `409` (approving from
`Planning`) and a `400` (an invalid id); a second project taken to `HumanReview` and
approved; then the audit log, deployment, database and configuration.

**Normalization** (`tool/fixture_normalizer.dart`): every GUID → a stable sequence GUID
(the same id maps to the same value in every file and inside messages); every instant →
`2026-01-01T00:00:00` + n seconds, **order preserved and the format preserved** — a
timestamp with a `Z` keeps it, one without stays without (the backend's documented
quirk); hashes → stable hex; OS, runtime, informational version and uptime → fixed
placeholders. Enum spellings, numeric ordinals, nulls and structure are kept exactly.

`agent_run_structured.json` is **hand-authored**: a structured result exists only
after a real agent execution, which the capture does not run. Its fields are checked
against a captured run on every `--check`.

### 5.2 Drift detection

```bash
dart run tool/api_fixtures.dart --base-url http://127.0.0.1:5299 --check
```

Captures a fresh API and compares each fixture's **contract** with the committed one:
field names, JSON types, nullability, enum spelling or ordinal, the timestamp format
(`Z` or not) and deterministic values. Ids and instants are tokens; counters and
runtime descriptions are compared by type; configuration-dependent lists
(`externalProviders`, `configurationSources`, resilience profile names) by the field
names their elements carry. Exit code 1 on any drift, with the path of every
difference. `test/contract/fixture_contract_test.dart` proves the comparison fails on a
new field, a removed field, an enum representation change, a gained or lost `Z`, a
type change and a nullability change — and ignores ids, instants and counters.

---

## 6. Security

* `tool/secret_scan.dart` (CI; also `test/security/secret_scan_test.dart`) scans
  `lib/`, `test/`, `integration_test/`, `assets/`, `tool/`, `pubspec.yaml` — and, after
  the builds, `build/web` and the Windows release folder — for secret **values**:
  private keys, OpenRouter/Anthropic/`sk-` keys, GitHub/AWS/Google/Slack tokens, JWTs,
  bearer values, `.env`-style assignments and credential-named fields holding a
  high-entropy literal. **References** — `OPENROUTER_API_KEY`, `X-Operations-Key`,
  `ApiHeaders.operationsKey`, translation keys, interpolations, masks — never match.
  A finding prints the line with the value masked.
* `credential_storage_test.dart`: the key goes to `flutter_secure_storage` and nowhere
  else (SharedPreferences checked), clearing removes it from the keystore and the live
  header, request/error log lines redact it and never log a body, a production build
  does not log at all, the key is never displayed after saving, and a Web build keeps
  it in memory for the session only (and says so).
* `release_config_test.dart`: a release build never defaults to the development API.
* Goldens contain no credential: they are rendered from the key-free catalog, and the
  operator-access panel never renders a stored key.

---

## 7. Performance

Synthetic, deterministic rendering data (never presented as API data). Widget-test
timings (debug JIT, software rendering) are upper bounds; the assertions that matter
are structural. Results: `build/perf/large_lists.json` (git-ignored).

| Suite | What it asserts |
|---|---|
| Projects 100 / 500 / 1 000 / 5 000 | The list is virtualized (≤ 40 rows built), one repository read, scrolling 20 screens re-reads nothing, first-frame cost does not grow like O(n²); filter + sort over all statuses stays in milliseconds |
| WorkItems 100 / 500 / 1 000 | One plan read (it carries the work items), no per-item reads; bounded build time |
| AgentRuns 100 / 500 / 1 000 | Rows are summary-only — 20 kB outputs and 200-item structured results are never rendered per row; the screen requests one page of 100 |
| Rebuilds | An unchanged poll rebuilds no row; a changed one does |
| Dependency trees | 50 / 100 / 500 nodes × linear, branching, converging, shared, malformed/cyclic: rows ≤ nodes + edges, every node shown, cycles marked; a 20 000-node chain flattens and validates without recursion; 500-node trees render at phone width with capped indentation |

The work-item and agent-run lists are not virtualized: they are sections inside the
Project Detail page, built when scrolled into view. Their size is bounded by the plan
and by the one page of 100 runs the screen requests.

---

## 8. Integration tests

All need `-d windows`; none runs under a plain `flutter test`.

| Test | Kind | Needs |
|---|---|---|
| `real_backend_lifecycle_test.dart` | Real backend | Throwaway API, `AI_FACTORY_LIVE=true` |
| `real_backend_operations_test.dart` | Real backend | same |
| `real_backend_diagnostics_test.dart` | Real backend | same |
| `soak_rate_limit_test.dart` | Real backend | same, fresh API; `AI_FACTORY_SOAK_MINUTES` |
| `live_polling_flow_test.dart`, `live_dashboard_polling_test.dart` | Real backend | throwaway API, `AI_FACTORY_LIVE_POLLING=true` |
| `operator_flow_test.dart` | Real backend | throwaway API on an **empty** database |
| `project_detail_flow_test.dart`, `work_items_flow_test.dart`, `agent_runs_flow_test.dart` | Real backend | throwaway API |
| `offline_flow_test.dart` | Real transport, no backend | a base URL nothing listens on |
| `operations_flow_test.dart`, `human_review_flow_test.dart` | Real app binding, **fake repositories** | nothing |

```bash
flutter test integration_test/real_backend_lifecycle_test.dart -d windows \
  --dart-define=AI_FACTORY_LIVE=true \
  --dart-define=AI_FACTORY_API_BASE_URL=http://127.0.0.1:5299
```

**Harness limits** (properties of `IntegrationTestWidgetsFlutterBinding` on this
machine, not of the application): one `testWidgets` per file — a second one re-enters
the still-mounted app; start the screen under test through `initialRoute` where
possible. The Phase 19.10 live tests follow both rules.

---

## 9. Soak

* **Deterministic** — `test/soak/polling_soak_test.dart` (see
  [`flutter-architecture.md`](flutter-architecture.md) §12.7). Results:
  `build/perf/polling_soak.json` (git-ignored).
* **Live** — `integration_test/soak_rate_limit_test.dart` against a fresh throwaway
  API: every live screen, repeatedly, with open → leave → reopen, open → back → open,
  project switches, language switches and app pause → resume; every request metered.
  It fails on any `429`, on more than three live coordinators, on any request while
  hidden or after leaving every live screen, and when the busiest minute reaches a
  backend limit. The report is printed as `LIVE-SOAK {json}`.

The backend limiters are global. Run the live soak on its own API instance, with
nothing else calling it.

---

## 10. CI

`.github/workflows/ci.yml`, credential-free:

| Job | Runs |
|---|---|
| `flutter-client` (Windows) | pinned Flutter; `dart format --set-exit-if-changed`; `flutter analyze`; secret scan of sources; translation coverage; fixture validation; the suite without goldens; the goldens; Windows and Web release builds; secret scan of the build output |
| `flutter-contract-fixtures` (Ubuntu) | builds the API, starts it on a throwaway SQLite file in `Development`, runs `tool/api_fixtures.dart --check`, stops the API it started, uploads the API log |

No job needs a provider credential, an operational key or a production API.
