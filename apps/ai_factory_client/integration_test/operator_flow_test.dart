import 'package:ai_factory_client/app/app.dart';
import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/presentation/dialogs/create_project_dialog.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/widgets/project_list_tile.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// The Phase 19.3 acceptance flow, run against a **live** AI Factory API in
/// the **real** application.
///
/// This is an integration test rather than a widget test on purpose. A widget
/// test cannot do this job: `TestWidgetsFlutterBinding` stubs `HttpClient` so
/// every request returns 400, and its fake-async zone trips Dio's timeout
/// timers under a request that is still in flight for real.
/// `IntegrationTestWidgetsFlutterBinding` runs the app on a real target with
/// real networking and a real clock.
///
/// **Precondition: the API is backed by an empty database.** The run asserts
/// the empty state before creating anything, which is the "empty database ->
/// empty state -> create project" scenario the phase asks for.
///
/// Start the API, then:
///
/// ```bash
/// flutter test integration_test/operator_flow_test.dart -d windows \
///   --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
/// ```
///
/// The flow, in the order the phase specifies:
///
///     launch -> Dashboard -> Projects -> create -> project appears
///       -> open detail -> back -> start -> refresh -> status updated
///       -> English -> German -> English
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('the operator flow, end to end, against a live API', (
    tester,
  ) async {
    // A clean language and theme preference, so the run starts in English
    // regardless of what the machine has stored.
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

    /// Scrolls the page's list until [finder] is built.
    ///
    /// A `ListView` only builds what is on screen, which is the point of
    /// virtualizing it — so anything below the fold has to be scrolled to
    /// before it can be asserted on.
    Future<void> scrollTo(Finder finder) async {
      await tester.scrollUntilVisible(
        finder,
        200,
        scrollable: find.descendant(
          of: find.byType(ListView),
          matching: find.byType(Scrollable),
        ),
      );
      await tester.pumpAndSettle();
    }

    // --- Dashboard ---------------------------------------------------------
    expect(find.text('Needs attention'), findsOneWidget);

    await scrollTo(find.text('Platform'));
    expect(find.text('Platform'), findsOneWidget);
    // Read from the real /health/live and /health/ready probes.
    expect(find.text('Reachable'), findsOneWidget);
    expect(find.text('Ready'), findsOneWidget);
    // Read from the real GET /api/scheduler/status.
    expect(find.text('Enabled'), findsOneWidget);

    // --- Projects ----------------------------------------------------------
    await tester.tap(find.text('Projects').first);
    await tester.pumpAndSettle();
    expect(Get.currentRoute, AppRoutes.projects);

    // An empty factory says so, and offers the way out of it. This is not a
    // failure state and must not look like one.
    expect(find.text('No projects yet'), findsOneWidget);
    expect(
      find.text('Create a project to give the Factory something to work on.'),
      findsOneWidget,
    );

    // --- Create ------------------------------------------------------------
    await tester.tap(find.widgetWithText(FilledButton, 'New project'));
    await tester.pumpAndSettle();
    expect(find.byType(CreateProjectDialog), findsOneWidget);

    final name = 'Live UI ${DateTime.now().millisecondsSinceEpoch}';
    await tester.enterText(find.byType(TextFormField).at(0), name);
    await tester.enterText(
      find.byType(TextFormField).at(1),
      'Created by the Phase 19.3 acceptance test.',
    );
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    // POST /api/projects succeeded and the flow advanced to its second step.
    expect(find.text('Source'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Initialize source'));
    await tester.pumpAndSettle();

    expect(
      find.text('Project created and its source initialized.'),
      findsOneWidget,
    );
    // The status shown is the one the server returned, not one assumed from
    // having called the endpoint.
    expect(find.text('Analyzing'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Close'));
    await tester.pumpAndSettle();

    // --- The project appears in the list ------------------------------------
    expect(find.text(name), findsOneWidget);

    final row = find.ancestor(
      of: find.text(name),
      matching: find.byType(ProjectListTile),
    );
    expect(row, findsOneWidget);

    // --- Open Project Detail, then come back --------------------------------
    await tester.tap(find.descendant(of: row, matching: find.text('Open')));
    await tester.pumpAndSettle();

    expect(find.byType(ProjectDetailPage), findsOneWidget);
    // The screen itself is covered by `project_detail_flow_test.dart`; what
    // matters here is only that the navigation lands on the real project.
    expect(find.text('Lifecycle'), findsOneWidget);
    // Read back by id from GET /api/projects/{id}.
    expect(find.text(name), findsWidgets);

    Get.back<void>();
    await tester.pumpAndSettle();
    expect(Get.currentRoute, AppRoutes.projects);

    // --- Start -------------------------------------------------------------
    final startButton = find.descendant(
      of: find.ancestor(
        of: find.text(name),
        matching: find.byType(ProjectListTile),
      ),
      matching: find.widgetWithText(OutlinedButton, 'Start'),
    );
    expect(
      startButton,
      findsOneWidget,
      reason: 'a project that has not been started offers Start',
    );

    await tester.tap(startButton);
    await tester.pumpAndSettle();

    // POST /api/projects/{id}/start succeeded, so the action is gone —
    // because the server said the project is started, not because the client
    // decided it.
    expect(
      find.descendant(
        of: find.ancestor(
          of: find.text(name),
          matching: find.byType(ProjectListTile),
        ),
        matching: find.widgetWithText(OutlinedButton, 'Start'),
      ),
      findsNothing,
    );

    // --- Refresh -----------------------------------------------------------
    await tester.tap(find.byTooltip('Refresh'));
    await tester.pumpAndSettle();

    expect(find.text(name), findsOneWidget);
    expect(
      find.descendant(
        of: find.ancestor(
          of: find.text(name),
          matching: find.byType(ProjectListTile),
        ),
        matching: find.widgetWithText(OutlinedButton, 'Start'),
      ),
      findsNothing,
      reason: 'the started state must survive a re-read from the API',
    );
    // The lifecycle has moved on from Created; the exact stage is the
    // Worker's business and is whatever the API reports.
    expect(find.text('Created'), findsNothing);

    // --- English -> German -> English ---------------------------------------
    var context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Neues Projekt'), findsOneWidget);
    expect(find.text('Projekte'), findsWidgets);
    // Operator data is never translated.
    expect(find.text(name), findsOneWidget);

    context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.english);
    await tester.pumpAndSettle();

    expect(find.text('New project'), findsOneWidget);
    expect(find.text('Neues Projekt'), findsNothing);

    // --- Back to the Dashboard, which now counts the new project -----------
    await tester.tap(find.text('Dashboard').first);
    await tester.pumpAndSettle();

    await scrollTo(find.text('Recently updated'));
    expect(find.text('Recently updated'), findsOneWidget);
    expect(find.text(name), findsWidgets);
  });
}
