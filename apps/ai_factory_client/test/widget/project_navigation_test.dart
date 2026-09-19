import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// Projects -> Project Detail, driven through the real routing table.
///
/// The repositories are registered **before** the route bindings run, which
/// is the substitution recipe the Phase 19.2 DI test pinned down: a `fenix`
/// registration cannot be replaced afterwards.
void main() {
  setUpAll(initializeTestLocalization);

  late FakeProjectRepository projects;
  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
    projects = FakeProjectRepository(
      projects: [
        testProject(
          id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
          name: 'Customer API',
          status: ProjectStatus.humanReview,
        ),
      ],
    );
    Get.put<ProjectRepository>(projects);
  });

  Future<void> pumpApp(WidgetTester tester, {String? route}) async {
    await pumpScreen(
      tester,
      initialRoute: route ?? AppRoutes.projects,
      getPages: AppRoutes.pages,
    );
  }

  testWidgets('opening a project navigates to the detail placeholder', (
    tester,
  ) async {
    await pumpApp(tester);
    expect(find.text('Customer API'), findsOneWidget);

    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    expect(find.byType(ProjectDetailPage), findsOneWidget);
    expect(find.text('Project detail'), findsOneWidget);
    // Honest about what it is not.
    expect(
      find.textContaining('The rest of it is not built yet'),
      findsOneWidget,
    );
  });

  testWidgets('the placeholder reads the project it was opened for', (
    tester,
  ) async {
    await pumpApp(tester);
    await tester.tap(find.text('Open'));
    await tester.pumpAndSettle();

    // Fetched by id, so a deep link works without the list.
    expect(
      projects.calls,
      contains('getById:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
    );
    expect(find.text('Customer API'), findsWidgets);
    expect(find.byType(StatusChip), findsOneWidget);
    expect(find.text('Human review'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });

  testWidgets('a deep link to an unknown project shows a not-found state', (
    tester,
  ) async {
    await pumpApp(tester, route: AppRoutes.projectDetail('does-not-exist'));

    expect(
      find.text('The requested resource no longer exists.'),
      findsOneWidget,
    );
    expect(find.text('Retry'), findsOneWidget);
  });

  testWidgets('a recorded backend error is shown on the placeholder', (
    tester,
  ) async {
    projects.projects = [
      testProject(
        id: 'p-err',
        name: 'Broken',
        status: ProjectStatus.failed,
        currentError: 'Planner produced no work items.',
      ),
    ];

    await pumpApp(tester, route: AppRoutes.projectDetail('p-err'));

    expect(find.text('Error recorded by the backend'), findsOneWidget);
    expect(find.text('Planner produced no work items.'), findsOneWidget);
  });

  testWidgets('a failed detail read offers a retry', (tester) async {
    projects.listError = const NetworkError();
    await pumpApp(tester, route: AppRoutes.projectDetail('p1'));

    expect(find.textContaining('No connection'), findsOneWidget);

    projects.listError = null;
    projects.projects = [testProject(id: 'p1', name: 'Recovered')];
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();

    expect(find.text('Recovered'), findsWidgets);
  });

  testWidgets('the placeholder renders in German', (tester) async {
    await pumpApp(
      tester,
      route: AppRoutes.projectDetail('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
    );

    final context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Projektdetails'), findsOneWidget);
    expect(find.text('Kennung'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });

  testWidgets('the placeholder lays out without overflow when compact', (
    tester,
  ) async {
    await pumpScreen(
      tester,
      initialRoute: AppRoutes.projectDetail(
        'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      ),
      getPages: AppRoutes.pages,
      size: TestSizes.compact,
    );

    expect(tester.takeException(), isNull);
    expect(find.byType(ProjectDetailPage), findsOneWidget);
  });

  test('the detail route is built from the project id', () {
    expect(AppRoutes.projectDetail('p1'), '/projects/p1');
    expect(AppRoutes.projectDetailPattern, '/projects/:projectId');
  });
}
