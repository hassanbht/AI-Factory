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

/// Phase 19.5's provable slice of the acceptance flow, run against a
/// **live** AI Factory API in the **real** application — see
/// `project_detail_flow_test.dart` for the run instructions (same throwaway
/// database recipe, same `-d windows` invocation).
///
/// **Workstream AC discipline.** `WorkItemRepository` and `ProjectRepository`
/// are read-only for work items and plans in this phase — there is no client
/// action that creates a task or submits a plan (Workstream AE/K: the graph
/// is read-only, and task/plan creation was never in this phase's scope).
/// That means a real, freshly created project is exactly what this test can
/// honestly prove: **no work items exist yet**, so the WorkItems section must
/// show the "no work items" state rather than a fabricated list — and it
/// must do so from the real `GET /api/projects/{id}/tasks` response, not a
/// mock. A populated plan/DAG is covered by the deterministic controller,
/// graph-projection and widget tests instead, which is the documented
/// fallback for a setup the current API surface cannot drive from the UI.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('WorkItems section against a live API: the empty state', (
    tester,
  ) async {
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

    /// Scrolls Project Detail's `ListView` until [finder] is built — see
    /// `revealInDetail` in `project_detail_flow_test.dart` for why this is
    /// necessary rather than a plain `find`.
    Future<void> reveal(Finder finder) async {
      if (finder.evaluate().isNotEmpty) return;
      final list = find
          .descendant(
            of: find.byType(ProjectDetailPage),
            matching: find.byType(ListView),
          )
          .first;
      for (var attempt = 0; attempt < 12; attempt++) {
        await tester.drag(list, const Offset(0, -600));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) return;
      }
    }

    // --- Create a project; its source is irrelevant to this flow ----------
    await tester.tap(find.text('Projects').first);
    await tester.pumpAndSettle();
    expect(Get.currentRoute, AppRoutes.projects);

    await tester.tap(find.widgetWithText(FilledButton, 'New project'));
    await tester.pumpAndSettle();
    expect(find.byType(CreateProjectDialog), findsOneWidget);

    final name = 'WorkItems UI ${DateTime.now().millisecondsSinceEpoch}';
    await tester.enterText(find.byType(TextFormField).at(0), name);
    await tester.enterText(
      find.byType(TextFormField).at(1),
      'Created by the Phase 19.5 acceptance test.',
    );
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();
    expect(find.text('Source'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Initialize source'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Close'));
    await tester.pumpAndSettle();

    // --- Open Project Detail ------------------------------------------------
    final row = find.ancestor(
      of: find.text(name),
      matching: find.byType(ProjectListTile),
    );
    await tester.tap(find.descendant(of: row, matching: find.text('Open')));
    await tester.pumpAndSettle();
    expect(find.byType(ProjectDetailPage), findsOneWidget);

    // --- The real API reports no work items for a brand-new project --------
    await reveal(find.text('No work items'));
    expect(find.text('No work items'), findsOneWidget);
    expect(
      find.text('This project does not currently expose any work items.'),
      findsOneWidget,
    );

    // --- English -> German -> English, for this section's own strings ------
    var context = tester.element(find.byType(Scaffold).first);
    final localeService = Get.find<LocaleService>();
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    await reveal(find.text('Keine Arbeitspakete'));
    expect(find.text('Keine Arbeitspakete'), findsOneWidget);

    context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.english);
    await tester.pumpAndSettle();

    await reveal(find.text('No work items'));
    expect(find.text('No work items'), findsOneWidget);
  });
}
