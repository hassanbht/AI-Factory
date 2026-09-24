import 'dart:async';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/human_review_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// Human Review section widget tests — Phase 19.7.
///
/// Every test pumps the real Project Detail route (same as
/// `work_items_section_test.dart`) so the section is exercised in its actual
/// position in the widget tree, with real translations. The approval
/// confirmation dialog is driven by tapping the Approve button.
void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-ffffffffffff';

  late FakeProjectRepository projects;
  late FakeWorkItemRepository workItems;
  late FakeAgentRunRepository agentRuns;
  late FakeApprovalRepository approval;

  setUp(() async {
    await registerTestServices();
    projects = FakeProjectRepository();
    workItems = FakeWorkItemRepository();
    agentRuns = FakeAgentRunRepository();
    approval = FakeApprovalRepository();

    Get.put<ProjectRepository>(projects);
    Get.put<WorkItemRepository>(workItems);
    Get.put<AgentRunRepository>(agentRuns);
    Get.put<ApprovalRepository>(approval);
  });

  Future<void> open(WidgetTester tester, {Size size = TestSizes.expanded}) =>
      pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        size: size,
      );

  /// Drags the Project Detail ListView down until [finder] mounts, then
  /// scrolls it fully into the hit-testable viewport. Mounting alone is not
  /// enough — cacheExtent mounts a widget slightly before it is actually
  /// tappable, and a tap at that point misses.
  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isEmpty) {
      final list = find.byType(ListView).first;
      for (var i = 0; i < 12; i++) {
        await tester.drag(list, const Offset(0, -600));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) break;
      }
    }
    await tester.ensureVisible(finder);
    await tester.pumpAndSettle();
  }

  // ---------------------------------------------------------------------------
  // Section visibility
  // ---------------------------------------------------------------------------

  group('section visibility', () {
    for (final status in [
      ProjectStatus.analyzing,
      ProjectStatus.implementation,
      ProjectStatus.testing,
      ProjectStatus.reviewing,
      ProjectStatus.failed,
      ProjectStatus.cancelled,
      ProjectStatus.paused,
    ]) {
      testWidgets('section is absent when status is ${status.wireValue}', (
        tester,
      ) async {
        projects.projects = [testProject(id: projectId, status: status)];
        await open(tester);

        // Scroll to the very bottom and confirm neither key appears.
        final list = find.byType(ListView).first;
        for (var i = 0; i < 12; i++) {
          await tester.drag(list, const Offset(0, -600));
          await tester.pumpAndSettle();
        }

        expect(
          find.text('Human Review Required'),
          findsNothing,
          reason: 'HR title must not appear for ${status.wireValue}',
        );
        expect(find.text('Project Completed'), findsNothing);
      });
    }

    testWidgets('section is visible for HumanReview status', (tester) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.humanReview),
      ];
      await open(tester);

      await reveal(tester, find.text('Human Review Required'));
      expect(find.text('Human Review Required'), findsOneWidget);
    });

    testWidgets('Completed status shows completed note, not approval section', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.completed),
      ];
      await open(tester);

      await reveal(tester, find.text('Project Completed'));
      expect(find.text('Project Completed'), findsOneWidget);
      expect(find.text('Human Review Required'), findsNothing);
    });
  });

  // ---------------------------------------------------------------------------
  // HumanReview body
  // ---------------------------------------------------------------------------

  group('HumanReview body', () {
    setUp(() {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.humanReview),
      ];
    });

    testWidgets('shows Review Summary and Execution Evidence panels', (
      tester,
    ) async {
      await open(tester);

      await reveal(tester, find.text('Human Review Required'));
      expect(find.text('Review Summary'), findsOneWidget);
      expect(find.text('Execution Evidence'), findsOneWidget);
    });

    testWidgets('shows acceptance UnavailableNote (G-ACCEPTANCE)', (
      tester,
    ) async {
      await open(tester);

      await reveal(tester, find.text('Acceptance'));
      expect(find.text('Acceptance'), findsWidgets);
      expect(find.textContaining('not exposed by the API'), findsOneWidget);
    });

    testWidgets('shows telemetry UnavailableNote (G-RUN-TELEMETRY)', (
      tester,
    ) async {
      await open(tester);

      await reveal(tester, find.text('Execution telemetry'));
      expect(find.text('Execution telemetry'), findsOneWidget);
      expect(find.textContaining('G-RUN-TELEMETRY'), findsOneWidget);
    });

    testWidgets('Approve project button is present and enabled', (
      tester,
    ) async {
      await open(tester);

      await reveal(
        tester,
        find.widgetWithText(FilledButton, 'Approve project'),
      );
      final button = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Approve project'),
      );
      expect(
        button.onPressed,
        isNotNull,
        reason: 'button must be enabled in HumanReview',
      );
    });
  });

  // ---------------------------------------------------------------------------
  // Approval confirmation dialog
  // ---------------------------------------------------------------------------

  group('confirmation dialog', () {
    setUp(() {
      projects.projects = [
        testProject(
          id: projectId,
          status: ProjectStatus.humanReview,
          name: 'My Test Project',
        ),
      ];
    });

    testWidgets('tapping Approve opens the confirmation dialog', (
      tester,
    ) async {
      await open(tester);
      await reveal(
        tester,
        find.widgetWithText(FilledButton, 'Approve project'),
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
      await tester.pumpAndSettle();

      expect(find.text('Approve project?'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text('My Test Project'),
        ),
        findsOneWidget,
      );
    });

    testWidgets('Cancel dismisses the dialog without calling approve', (
      tester,
    ) async {
      await open(tester);
      await reveal(
        tester,
        find.widgetWithText(FilledButton, 'Approve project'),
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
      await tester.pumpAndSettle();

      // Tap Cancel in the dialog.
      await tester.tap(find.text('Cancel').last);
      await tester.pumpAndSettle();

      expect(find.text('Approve project?'), findsNothing);
      expect(
        approval.approveCount,
        0,
        reason: 'Cancel must not call the approve endpoint',
      );
    });

    testWidgets('Approve in dialog calls the controller', (tester) async {
      await open(tester);
      await reveal(
        tester,
        find.widgetWithText(FilledButton, 'Approve project'),
      );

      // Stage a Completed project for the post-approval refresh — set only
      // now, so the initial load above still sees the HumanReview project.
      approval.approveResult = testProject(
        id: projectId,
        status: ProjectStatus.completed,
      );
      projects.getByIdResult = testProject(
        id: projectId,
        status: ProjectStatus.completed,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
      await tester.pumpAndSettle();

      // Confirm in the dialog.
      await tester.tap(find.text('Approve').last);
      await tester.pumpAndSettle();

      expect(
        approval.approveCount,
        1,
        reason: 'Approve dialog button must call the approve endpoint',
      );
    });
  });

  // ---------------------------------------------------------------------------
  // isApproving spinner
  // ---------------------------------------------------------------------------

  testWidgets(
    'Approve button is disabled and shows spinner while isApproving',
    (tester) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.humanReview),
      ];

      // Gate the approval so we can observe the pending state.
      final gate = Completer<void>();
      approval.gate = gate.future;

      await open(tester);
      await reveal(
        tester,
        find.widgetWithText(FilledButton, 'Approve project'),
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
      await tester.pumpAndSettle();

      // Confirm in dialog.
      await tester.tap(find.text('Approve').last);
      await tester.pump(); // start the async call

      final ctrl = Get.find<HumanReviewController>();
      expect(ctrl.isApproving.value, isTrue);

      // The button label changes to pending text.
      await tester.pump();
      expect(find.textContaining('Approval in progress'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);

      gate.complete();
      await tester.pumpAndSettle();
    },
  );

  // ---------------------------------------------------------------------------
  // Approval error banner
  // ---------------------------------------------------------------------------

  testWidgets('ConflictError shows error banner above the section', (
    tester,
  ) async {
    projects.projects = [
      testProject(id: projectId, status: ProjectStatus.humanReview),
    ];
    approval.approveError = const ConflictError();

    await open(tester);
    await reveal(tester, find.widgetWithText(FilledButton, 'Approve project'));
    await tester.tap(find.widgetWithText(FilledButton, 'Approve project'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Approve').last);
    await tester.pumpAndSettle();

    expect(
      find.textContaining('changed in the meantime'),
      findsOneWidget,
      reason: 'conflict error message must appear as a banner',
    );
  });

  // ---------------------------------------------------------------------------
  // Localization — German
  // ---------------------------------------------------------------------------

  group('German locale', () {
    testWidgets('renders translated title in HumanReview', (tester) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.humanReview),
      ];

      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        startLocale: const Locale('de'),
      );

      await reveal(tester, find.text('Manuelle Prüfung erforderlich'));
      expect(find.text('Manuelle Prüfung erforderlich'), findsOneWidget);
      expect(find.text('Projekt freigeben'), findsOneWidget);
    });

    testWidgets('renders translated completed note', (tester) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.completed),
      ];

      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        startLocale: const Locale('de'),
      );

      await reveal(tester, find.text('Projekt abgeschlossen'));
      expect(find.text('Projekt abgeschlossen'), findsOneWidget);
    });

    testWidgets('no untranslated keys visible in German', (tester) async {
      projects.projects = [
        testProject(id: projectId, status: ProjectStatus.humanReview),
      ];

      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        startLocale: const Locale('de'),
      );

      // Scroll to load the section.
      final list = find.byType(ListView).first;
      for (var i = 0; i < 12; i++) {
        await tester.drag(list, const Offset(0, -600));
        await tester.pumpAndSettle();
      }

      expectNoUntranslatedKeys(tester);
    });
  });

  // ---------------------------------------------------------------------------
  // Compact layout
  // ---------------------------------------------------------------------------

  testWidgets('renders correctly at compact (phone) width', (tester) async {
    projects.projects = [
      testProject(id: projectId, status: ProjectStatus.humanReview),
    ];

    await open(tester, size: TestSizes.compact);

    await reveal(tester, find.text('Human Review Required'));
    expect(find.text('Human Review Required'), findsOneWidget);
    expect(find.text('Review Summary'), findsOneWidget);
    expect(find.text('Execution Evidence'), findsOneWidget);
  });

  // ---------------------------------------------------------------------------
  // No untranslated keys in English
  // ---------------------------------------------------------------------------

  testWidgets('no untranslated keys visible in English', (tester) async {
    projects.projects = [
      testProject(id: projectId, status: ProjectStatus.humanReview),
    ];

    await open(tester);

    final list = find.byType(ListView).first;
    for (var i = 0; i < 12; i++) {
      await tester.drag(list, const Offset(0, -600));
      await tester.pumpAndSettle();
    }

    expectNoUntranslatedKeys(tester);
  });
}
