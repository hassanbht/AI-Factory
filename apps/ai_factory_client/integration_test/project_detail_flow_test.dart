import 'package:ai_factory_client/app/app.dart';
import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/presentation/dialogs/create_project_dialog.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/widgets/project_lifecycle_widget.dart';
import 'package:ai_factory_client/presentation/widgets/project_list_tile.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// The Phase 19.4 acceptance flow, run against a **live** AI Factory API in
/// the **real** application.
///
/// A widget test cannot do this job: `TestWidgetsFlutterBinding` stubs
/// `HttpClient` so every request answers 400, and its fake-async zone trips
/// Dio's timeout timers under a request that is genuinely in flight.
/// `IntegrationTestWidgetsFlutterBinding` runs the app on a real target with
/// real networking and a real clock.
///
/// **Use a throwaway database.** The run creates and starts a project, so
/// point the API at a scratch file rather than at the operator's own data:
///
/// ```powershell
/// $env:ConnectionStrings__FactoryDatabase = "Data Source=$env:TEMP/ai-factory-19-4.db"
/// dotnet run --project apps/src/AI.Factory.Api
/// ```
///
/// Then, from `apps/ai_factory_client`:
///
/// ```powershell
/// flutter test integration_test/project_detail_flow_test.dart -d windows `
///   --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
/// ```
///
/// The flow, in the order Phase 19.4 specifies:
///
///     create -> initialize source -> open Project Detail
///       -> verify identity -> verify the reported status
///       -> Start -> refresh -> verify the status the server now reports
///       -> English -> German -> English
///
/// What it deliberately does **not** assert is a particular lifecycle stage
/// after Start. The Worker owns how far the project has advanced by the time
/// the screen re-reads it, so the test asserts that the screen shows *the
/// server's* answer — a localized, known state and a recorded start time —
/// rather than a stage this client decided on.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('Project Detail against a live API, end to end', (tester) async {
    // English pinned, not left to the host: on a German Windows machine the
    // "system" preference correctly resolves to German, and every English
    // assertion below would fail for a reason that is not a defect.
    SharedPreferences.setMockInitialValues(<String, Object>{
      LocaleService.storageKey: LocalePreference.english.storageValue,
    });
    await EasyLocalization.ensureInitialized();
    await Get.deleteAll(force: true);
    await AppBindings.initPreRunApp();

    await tester.pumpWidget(const AiFactoryAppRoot());
    await tester.pumpAndSettle();

    final localeService = Get.find<LocaleService>();

    /// Brings [finder] into the built part of the detail screen.
    ///
    /// A `ListView` only builds what is on screen, so anything off the fold
    /// has to be scrolled to before it can be asserted on — and the target may
    /// be either side of where the list currently sits, so this goes back to
    /// the top and then walks down.
    Future<void> revealInDetail(Finder finder) async {
      if (finder.evaluate().isNotEmpty) return;

      final list = find
          .descendant(
            of: find.byType(ProjectDetailPage),
            matching: find.byType(ListView),
          )
          .first;

      for (var attempt = 0; attempt < 8; attempt++) {
        await tester.drag(list, const Offset(0, 600));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) return;
      }
      for (var attempt = 0; attempt < 12; attempt++) {
        await tester.drag(list, const Offset(0, -300));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) return;
      }
    }

    /// The status the **header chip** is showing, which is the status the
    /// server reported on the last read.
    String reportedStatus() =>
        tester.widget<StatusChip>(find.byType(StatusChip).first).label;

    /// Every project state, as this build localizes it. Used to assert that
    /// whatever the server reported rendered as a translated word rather than
    /// as a raw wire value or a key.
    const englishStates = <String>[
      'Created',
      'Analyzing',
      'Specification',
      'Architecture',
      'Planning',
      'Implementation',
      'Paused',
      'Testing',
      'Repairing',
      'Reviewing',
      'Human review',
      'Completed',
      'Failed',
      'Cancelled',
    ];

    // --- Create a project with an initialized source -----------------------
    await tester.tap(find.text('Projects').first);
    await tester.pumpAndSettle();
    expect(Get.currentRoute, AppRoutes.projects);

    await tester.tap(find.widgetWithText(FilledButton, 'New project'));
    await tester.pumpAndSettle();
    expect(find.byType(CreateProjectDialog), findsOneWidget);

    final name = 'Detail UI ${DateTime.now().millisecondsSinceEpoch}';
    const description = 'Created by the Phase 19.4 acceptance test.';

    await tester.enterText(find.byType(TextFormField).at(0), name);
    await tester.enterText(find.byType(TextFormField).at(1), description);
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();
    expect(find.text('Source'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Initialize source'));
    await tester.pumpAndSettle();
    expect(
      find.text('Project created and its source initialized.'),
      findsOneWidget,
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Close'));
    await tester.pumpAndSettle();

    // --- Open Project Detail ----------------------------------------------
    final row = find.ancestor(
      of: find.text(name),
      matching: find.byType(ProjectListTile),
    );
    expect(row, findsOneWidget);

    await tester.tap(find.descendant(of: row, matching: find.text('Open')));
    await tester.pumpAndSettle();

    expect(find.byType(ProjectDetailPage), findsOneWidget);
    expect(Get.currentRoute, startsWith('/projects/'));

    // --- Identity, read back from GET /api/projects/{id} -------------------
    expect(find.text(name), findsWidgets);
    // Below the header and the lifecycle track: scrolled to, as an operator
    // would, on a window too short to show it at once.
    await revealInDetail(find.text(description));
    expect(find.text(description), findsOneWidget);
    // Back to the top, where the header chip the rest of the walk reads is.
    await tester.drag(
      find
          .descendant(
            of: find.byType(ProjectDetailPage),
            matching: find.byType(ListView),
          )
          .first,
      const Offset(0, 3000),
    );
    await tester.pumpAndSettle();
    // Operator data is rendered verbatim, so the description is not translated
    // and not truncated.

    // --- The reported status ----------------------------------------------
    final beforeStart = reportedStatus();
    expect(
      englishStates,
      contains(beforeStart),
      reason: 'the server state must render as a localized label',
    );

    expect(find.byType(ProjectLifecycleWidget), findsOneWidget);
    expect(find.text('Current state'), findsOneWidget);
    expect(find.text('Current stage'), findsOneWidget);
    // The screen never claims a history the API does not expose.
    await revealInDetail(find.text('Stage history'));
    expect(
      find.textContaining('The API reports the current status only'),
      findsOneWidget,
    );

    // The source step succeeded, so the project is startable and says so.
    expect(find.text('Not started'), findsOneWidget);

    // --- Start -------------------------------------------------------------
    await revealInDetail(find.widgetWithText(FilledButton, 'Start'));
    await tester.tap(find.widgetWithText(FilledButton, 'Start'));
    await tester.pumpAndSettle();

    // POST /api/projects/{id}/start succeeded, so the action is gone —
    // because the server returned a started project, not because the client
    // decided it had.
    expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    expect(find.text('Not started'), findsNothing);

    // --- Refresh -----------------------------------------------------------
    await tester.tap(find.byTooltip('Refresh'));
    await tester.pumpAndSettle();

    final afterRefresh = reportedStatus();
    expect(
      englishStates,
      contains(afterRefresh),
      reason: 'the re-read state must render as a localized label',
    );
    // Whatever the Worker has done since, the project is no longer the
    // unstarted one it was before.
    expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    expect(find.text(name), findsWidgets);

    // --- English -> German -> English --------------------------------------
    var context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    for (final german in <String>[
      'Lebenszyklus',
      'Aktueller Zustand',
      'Übersicht',
      'Quelle',
    ]) {
      await revealInDetail(find.text(german));
      expect(find.text(german), findsOneWidget, reason: german);
    }

    // Operator data stays untranslated across the switch.
    await revealInDetail(find.text(description));
    expect(find.text(description), findsOneWidget);
    expect(find.text(name), findsWidgets);

    context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.english);
    await tester.pumpAndSettle();

    await revealInDetail(find.text('Lifecycle'));
    expect(find.text('Lifecycle'), findsOneWidget);
    expect(find.text('Lebenszyklus'), findsNothing);

    // --- Back to Projects, with the list still there -----------------------
    await tester.tap(find.byType(BackButton));
    await tester.pumpAndSettle();

    expect(Get.currentRoute, AppRoutes.projects);
    expect(find.text(name), findsOneWidget);
  });
}
