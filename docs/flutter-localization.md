# Flutter Client — Localization

Phase 19.1 · Application: `apps/ai_factory_client`

Two rules govern everything below:

    Backend enum  ≠  localized display string
    GetX          ≠  localization engine

---

## 1. Stack

| Concern | Owner |
|---|---|
| Translations, runtime switching, saved locale | **`easy_localization` 3.0.8** (pinned `^3.0.8`; the 4.x prerelease line is not used) |
| Locale *preference* (System / English / Deutsch) and its persistence | `LocaleService` (`lib/core/localization/locale_service.dart`) |
| Dependency injection, state, navigation | GetX — never translations |
| Date, number and duration formats | `intl` via `lib/core/utils/formatters.dart` |

`gen_l10n`/ARB is **not** used for application translations in this project.

## 2. Files

    assets/translations/
      en.json   # reference file
      de.json   # must contain every key en.json contains

Declared in `pubspec.yaml`:

    flutter:
      assets:
        - assets/translations/

`EasyLocalization.ensureInitialized()` runs in `main()` before `runApp`, so the
first frame is already in the right language.

## 3. Configuration

    supportedLocales : en, de      (LocaleService.supportedLocales)
    fallbackLocale   : en          (LocaleService.fallbackLocale)
    path             : assets/translations
    useFallbackTranslations: true  (a missing key falls back to English rather than rendering the key)
    saveLocale       : package default (true) — easy_localization persists the active locale

## 4. Key naming

Semantic, dotted, namespaced by area — never an English sentence as a key.

    app.title              nav.dashboard            common.retry
    state.loading.message  state.error.title        error.network
    settings.appearance.language                    placeholder.projects
    enums.projectStatus.humanReview                 enums.agentRunStatus.timedOut

Usage: `'nav.projects'.tr()`, or `'enums.unknown'.tr(args: [rawValue])` when a
value has to be interpolated.

Rule 1 — **no user-visible hard-coded string in a widget.**

    Text('Projects')          // wrong
    Text('nav.projects'.tr()) // right

## 5. Backend enums

Wire values are identifiers and are never translated, never localized on the
way back to the API. `lib/domain/enums/backend_enums.dart` mirrors the backend
enums with their exact wire strings plus an `unknown` member;
`lib/core/localization/enum_labels.dart` maps a parsed value to a translation
key.

| Backend | English | Deutsch |
|---|---|---|
| `HumanReview` | Human review | Menschliche Freigabe |
| `Repairing` | Repairing | Reparatur |
| `Reviewing` | Reviewing | Prüfung |
| `Completed` | Completed | Abgeschlossen |
| `TimedOut` | Timed out | Zeitüberschreitung |

Covered enums: `ProjectStatus`, `WorkItemStatus`, `AgentRunStatus`,
`FailureCategory`.

A value this client cannot parse renders as `Unknown (RawValue)` /
`Unbekannt (RawValue)` — visible, never blank, and never the bare wire value
presented as if it were a label.

## 6. Language selection

Three choices, exposed in **Settings → Appearance and language**:

| Choice | Behaviour |
|---|---|
| System default | Follows the operating-system language when it is `en` or `de`; otherwise English |
| English | Always English |
| Deutsch | Always German |

Resolution order at startup:

1. the operator's saved explicit choice,
2. otherwise the device locale, when supported,
3. otherwise English.

The tri-state *preference* is persisted by `LocaleService` under
`settings.locale_preference` (`shared_preferences`); the resulting *locale* is
persisted by `easy_localization` itself. The package has no notion of "follow
the system", which is the only reason `LocaleService` exists.

`LocaleService.apply` uses the package's own runtime API:

* explicit choice → `context.setLocale(target)`
* System default → `context.deleteSaveLocale()` then `context.resetLocale()`

Switching takes effect immediately, with no application restart.

### The one GetX interaction

`GetMaterialApp` resolves its locale as `Get.locale ?? locale`, so GetX's cached
locale would otherwise pin the `MaterialApp` to the language it was first built
with. `LocaleService.apply` therefore assigns `Get.locale = target` after the
switch. That single assignment is the entire extent of GetX's involvement: the
decision, the translations, the loading and the persistence all belong to
`easy_localization` and `LocaleService`.

### Why the shell reads `context.locale`

`.tr()` reads a static translation table, so a widget has no dependency on the
locale unless it asks for one. `AppShell.build` reads `context.locale` to
register that dependency, and takes its page content as a `WidgetBuilder`
rather than a prebuilt widget — otherwise Flutter would skip the page subtree
on rebuild and leave it in the previous language.

## 7. What is localized, and what is not

| Localized | Never localized |
|---|---|
| Labels, titles, buttons, tooltips, semantic labels | Wire values sent to or received from the API |
| Display labels for backend enums | Project names, work item titles (operator data) |
| Error messages derived from `AppError.messageKey` | Server `detail` text (English, operator diagnostics only) |
| Dates, times, durations, numbers | Ids, correlation ids, plan keys, model names, git refs |

## 8. Layout

German UI strings run 20–35 % longer than English. No layout may be sized to
English text: the extended navigation rail reserves 208 dp, buttons and chips
size to content, and every breakpoint is verified with German strings by the
widget tests.

## 9. Tests

`flutter test` covers:

| Test | Guarantee |
|---|---|
| `test/localization/translation_files_test.dart` | both files exist and parse; identical key sets; no empty value; German is genuinely translated; every enum label key exists in both files; supported locales are exactly `en`/`de` with `en` as fallback |
| `test/localization/locale_service_test.dart` | resolution order; country-insensitive matching (`de_AT` → `de`); fallback to English for an unsupported device locale; persistence across a restart; an unrecognised stored value degrades to System; runtime switch both ways; reset to system |
| `test/localization/enum_labels_test.dart` | every enum value has a real label in both languages; labels differ between languages; unknown values show the raw value; wire values match the backend exactly |
| `test/widget/app_shell_test.dart` | the shell renders in English, switches to German and back with no restart, no translation key leaks into the UI, and German does not overflow at compact, tablet or desktop width |

Widget tests load the real JSON through `SyncFileAssetLoader`
(`test/support/test_app.dart`) instead of the root asset bundle: bundle access
is real I/O, which the fake-async zone of a widget test cannot advance, so the
localization delegate would stay pending forever. `AiFactoryAppRoot` takes an
optional `assetLoader` purely for this; production always uses the package
default.

## 10. Adding a translation

1. Add the key to `assets/translations/en.json`, in the right namespace.
2. Add the same key to `assets/translations/de.json` with natural UI German —
   not a literal rendering of the English.
3. Use `'your.key'.tr()` in the widget. Never a literal string.
4. For a new backend enum value, add it to `backend_enums.dart`, add its label
   key to both JSON files, and let `EnumLabels.allKeys()` pick it up — the
   translation test then fails automatically until both languages are present.
5. Run `flutter test`. A missing German key fails the build rather than
   silently falling back.

## 11. Terminology (keep consistent)

| English | Deutsch |
|---|---|
| Project | Projekt |
| Work item | Arbeitspaket |
| Agent run | Agentenlauf |
| Review | Prüfung |
| Approval | Freigabe |
| Repair | Reparatur |
| Verification | Verifizierung |
| Diagnostics | Diagnose |
| Operations | Betrieb |
| Settings | Einstellungen |
| Dashboard | Übersicht |
