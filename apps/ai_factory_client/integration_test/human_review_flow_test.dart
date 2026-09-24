import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/fake_repositories.dart';
import '../test/support/test_app.dart';

/// Phase 19.7 Human Review integration tests.
///
/// ## Live-flow status (Workstream BI)
///
/// A live `HumanReview → Completed` flow requires a backend that has:
///   1. Reached `HumanReview` via the autonomous pipeline.
///   2. A valid project ID in that state.
///
/// In the current CI environment the **ModelRoutingPreflight blocker**
/// (re-verified in Phases 19.5, 19.6 and 19.7) prevents the Worker from
/// reaching `HumanReview` autonomously. This is a backend configuration
/// limitation, not a client defect.
///
/// These tests therefore cover the **provable** client paths:
///
/// - **Offline path** — the client correctly hides the section for projects
///   that are not in `HumanReview` or `Completed`.
/// - **Offline mock path** — the client correctly renders the `HumanReview`
///   section using a scripted [FakeApprovalRepository]. The approval mutation
///   path through [HumanReviewController] is exercised end-to-end inside the
///   widget tree.
///
/// A live approval is **not** claimed unless this file is run against a real
/// backend that has genuinely reached `HumanReview`. At that point the live
/// test below can be enabled and pointed at the real project ID.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(initializeTestLocalization);

  const projectId = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff';

  late FakeProjectRepository projects;
  late FakeApprovalRepository approval;

  setUp(() async {
    await registerTestServices();
    projects = FakeProjectRepository();
    Get.put<ProjectRepository>(projects);
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    approval = FakeApprovalRepository();
    Get.put<ApprovalRepository>(approval);
  });

  // English pinned: on a German host the system locale is German, and the
  // English assertions below would pass vacuously (findsNothing) or fail.
  Future<void> open(WidgetTester tester) => pumpScreen(
    tester,
    initialRoute: AppRoutes.projectDetail(projectId),
    getPages: AppRoutes.pages,
    startLocale: const Locale('en'),
  );

  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isNotEmpty) return;
    final list = find.byType(ListView).first;
    for (var i = 0; i < 12; i++) {
      await tester.drag(list, const Offset(0, -600));
      await tester.pumpAndSettle();
      if (finder.evaluate().isNotEmpty) return;
    }
  }

  // ---------------------------------------------------------------------------
  // Offline path — non-HumanReview project
  // ---------------------------------------------------------------------------

  testWidgets('Human Review section is absent for a non-HumanReview project', (
    tester,
  ) async {
    projects.projects = [
      testProject(id: projectId, status: ProjectStatus.implementation),
    ];

    await open(tester);

    final list = find.byType(ListView).first;
    for (var i = 0; i < 12; i++) {
      await tester.drag(list, const Offset(0, -600));
      await tester.pumpAndSettle();
    }

    expect(find.text('Human Review Required'), findsNothing);
    expect(find.widgetWithText(FilledButton, 'Approve project'), findsNothing);
  });

  // ---------------------------------------------------------------------------
  // Offline mock path — HumanReview → Completed via FakeApprovalRepository
  // ---------------------------------------------------------------------------

  testWidgets(
    'Human Review section renders and approval flow completes via mock',
    (tester) async {
      projects.projects = [
        testProject(
          id: projectId,
          status: ProjectStatus.humanReview,
          name: 'Integration Test Project',
        ),
      ];
      await open(tester);

      // Section visible.
      await reveal(tester, find.text('Human Review Required'));
      expect(find.text('Human Review Required'), findsOneWidget);
      expect(
        find.widgetWithText(FilledButton, 'Approve project'),
        findsOneWidget,
      );

      // Stage the post-approval refresh — set only now, so the initial load
      // above still sees the HumanReview project.
      approval.approveResult = testProject(
        id: projectId,
        status: ProjectStatus.completed,
      );
      projects.getByIdResult = testProject(
        id: projectId,
        status: ProjectStatus.completed,
      );

      // Tap Approve — scrolled into the real window first: on a desktop
      // window of ordinary height the button sits below the fold.
      final approve = find.widgetWithText(FilledButton, 'Approve project');
      await tester.ensureVisible(approve);
      await tester.pumpAndSettle();
      await tester.tap(approve);
      await tester.pumpAndSettle();

      // Confirmation dialog.
      expect(find.text('Approve project?'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text('Integration Test Project'),
        ),
        findsOneWidget,
      );

      // Cancel first — no approve call.
      await tester.tap(find.text('Cancel').last);
      await tester.pumpAndSettle();
      expect(approval.approveCount, 0);

      // Tap Approve again and confirm.
      await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Approve').last);
      await tester.pumpAndSettle();

      expect(approval.approveCount, 1);

      // After approval the section should show the Completed note.
      await reveal(tester, find.text('Project Completed'));
      expect(find.text('Project Completed'), findsOneWidget);
      expect(find.text('Human Review Required'), findsNothing);
    },
  );

  // Language-switch (EN → DE → EN) coverage lives in the German-locale group
  // of human_review_section_test.dart — no separate integration test here.

  // ---------------------------------------------------------------------------
  // Live test — commented out until backend reaches HumanReview
  // ---------------------------------------------------------------------------
  //
  // To run against a real backend that has a project in HumanReview:
  //
  //   flutter test integration_test/human_review_flow_test.dart \
  //     -d windows \
  //     --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223 \
  //     --dart-define=LIVE_PROJECT_ID=<actual-project-id>
  //
  // Then un-skip:
  //
  // testWidgets('live approval: HumanReview → Completed', (tester) async {
  //   // Get.put<ProjectRepository>(ApiProjectRepository(Get.find<ApiClient>()));
  //   // ... real repositories ...
  //   // final projectId = const String.fromEnvironment('LIVE_PROJECT_ID');
  //   // await open(tester);
  //   // ... assert HumanReview visible, tap Approve, confirm Completed ...
  // }, skip: 'Requires backend in HumanReview state. See comment above.');
}
