import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/pages/projects_page.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// Projects -> Project Detail -> back, driven through the real routing table.
///
/// The screen's own content is covered by `project_detail_page_test.dart`;
/// what is pinned down here is the navigation contract: the id travels in the
/// route, the screen re-reads from the API rather than trusting the list's
/// copy, and the way back exists whether the screen was pushed or deep-linked.
///
/// The repositories are registered **before** the route bindings run, which is
/// the substitution recipe the Phase 19.2 DI test pinned down: a `fenix`
/// registration cannot be replaced afterwards.
void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

  late FakeProjectRepository projects;

  setUp(() async {
    await registerTestServices();
    projects = FakeProjectRepository(
      projects: [
        testProject(
          id: projectId,
          name: 'Customer API',
          status: ProjectStatus.humanReview,
          lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
        ),
      ],
    );
    Get.put<ProjectRepository>(projects);
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    Get.put<ApprovalRepository>(FakeApprovalRepository());
  });

  Future<void> pumpApp(WidgetTester tester, {String? route}) async {
    await pumpScreen(
      tester,
      initialRoute: route ?? AppRoutes.projects,
      getPages: AppRoutes.pages,
    );
  }

  testWidgets('opening a project navigates to its detail screen', (
    tester,
  ) async {
    await pumpApp(tester);
    expect(find.text('Customer API'), findsOneWidget);

    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    expect(find.byType(ProjectDetailPage), findsOneWidget);
    expect(Get.currentRoute, AppRoutes.projectDetail(projectId));
  });

  testWidgets('the screen re-reads the project by id rather than trusting '
      'the list', (tester) async {
    await pumpApp(tester);
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    expect(projects.calls, contains('getById:$projectId'));
    expect(find.text('Customer API'), findsWidgets);
    expect(find.byType(StatusChip), findsOneWidget);
    expect(find.text('Human review'), findsWidgets);
    expectNoUntranslatedKeys(tester);
  });

  testWidgets('back returns to the Projects screen', (tester) async {
    await pumpApp(tester);
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    await tester.tap(find.byType(BackButton));
    await tester.pumpAndSettle();

    expect(Get.currentRoute, AppRoutes.projects);
    expect(find.byType(ProjectsPage), findsOneWidget);
  });

  testWidgets('a deep link opens the screen without the list', (tester) async {
    await pumpApp(tester, route: AppRoutes.projectDetail(projectId));

    expect(find.byType(ProjectDetailPage), findsOneWidget);
    expect(find.text('Customer API'), findsWidgets);
    expect(projects.listCount, 0, reason: 'the list is not fetched for detail');
  });

  testWidgets('a deep link to an unknown project offers the way back', (
    tester,
  ) async {
    await pumpApp(tester, route: AppRoutes.projectDetail('does-not-exist'));

    expect(find.text('Project not found'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Back to projects'));
    await tester.pumpAndSettle();

    expect(Get.currentRoute, AppRoutes.projects);
  });

  test('the detail route is built from the project id', () {
    expect(AppRoutes.projectDetail('p1'), '/projects/p1');
    expect(AppRoutes.projectDetailPattern, '/projects/:projectId');
  });
}
