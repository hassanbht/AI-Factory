import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/projects_controller.dart';
import 'package:ai_factory_client/presentation/pages/projects_page.dart';
import 'package:ai_factory_client/presentation/widgets/project_list_tile.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

void main() {
  setUpAll(initializeTestLocalization);

  late FakeProjectRepository projects;
  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
    projects = FakeProjectRepository();
  });

  Future<ProjectsController> pump(
    WidgetTester tester, {
    Size size = TestSizes.expanded,
  }) async {
    final controller = ProjectsController(projects: projects);
    Get.put<ProjectsController>(controller);
    await pumpScreen(tester, home: const ProjectsPage(), size: size);
    return controller;
  }

  group('list', () {
    testWidgets('renders a row per project with its status', (tester) async {
      projects.projects = [
        testProject(
          id: '1',
          name: 'Customer API',
          status: ProjectStatus.implementation,
        ),
        testProject(
          id: '2',
          name: 'Mobile App',
          status: ProjectStatus.humanReview,
        ),
      ];
      await pump(tester);

      expect(find.byType(ProjectListTile), findsNWidgets(2));
      expect(find.text('Customer API'), findsOneWidget);
      expect(find.text('Mobile App'), findsOneWidget);

      // The wire value is never what the operator sees. ("Human review" also
      // labels the filter chip, so the assertion is on the status chip.)
      expect(find.text('HumanReview'), findsNothing);
      expect(find.byType(StatusChip), findsNWidgets(2));
      expect(
        find.descendant(
          of: find.byType(StatusChip),
          matching: find.text('Human review'),
        ),
        findsOneWidget,
      );
      expectNoUntranslatedKeys(tester);
    });

    testWidgets('an unknown status shows the raw value rather than a blank', (
      tester,
    ) async {
      projects.projects = [
        testProject(
          id: '1',
          status: ProjectStatus.unknown,
          rawStatus: 'Negotiating',
        ),
      ];
      await pump(tester);

      expect(find.text('Unknown (Negotiating)'), findsOneWidget);
    });

    testWidgets('a project that has not initialized its source says so', (
      tester,
    ) async {
      projects.projects = [testProject(status: ProjectStatus.created)];
      await pump(tester);

      expect(find.text('Source not initialized'), findsOneWidget);
    });

    testWidgets('an empty factory offers the create action', (tester) async {
      await pump(tester);

      expect(find.text('No projects yet'), findsOneWidget);
      expect(
        find.text('Create a project to give the Factory something to work on.'),
        findsOneWidget,
      );
      expect(find.widgetWithText(FilledButton, 'New project'), findsOneWidget);
    });

    testWidgets('a failed load shows a localized error and retries', (
      tester,
    ) async {
      projects.listError = const TimeoutError();
      await pump(tester);

      expect(find.text('The operation timed out.'), findsOneWidget);

      projects.listError = null;
      projects.projects = [testProject(name: 'Customer API')];
      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      expect(find.text('Customer API'), findsOneWidget);
    });
  });

  group('filtering', () {
    setUp(() {
      projects.projects = [
        testProject(
          id: '1',
          name: 'Customer API',
          status: ProjectStatus.implementation,
        ),
        testProject(
          id: '2',
          name: 'Mobile App',
          status: ProjectStatus.humanReview,
        ),
      ];
    });

    testWidgets('a status chip narrows the list', (tester) async {
      await pump(tester);
      expect(find.byType(ProjectListTile), findsNWidgets(2));

      await tester.tap(find.widgetWithText(ChoiceChip, 'Human review'));
      await tester.pumpAndSettle();

      expect(find.byType(ProjectListTile), findsOneWidget);
      expect(find.text('Mobile App'), findsOneWidget);
    });

    testWidgets('a search that matches nothing explains itself', (
      tester,
    ) async {
      await pump(tester);

      await tester.enterText(find.byType(TextField).first, 'zzz');
      await tester.pumpAndSettle();

      // Distinct from "no projects exist", which would be misleading here.
      expect(find.text('No project matches this filter'), findsOneWidget);
      expect(find.text('No projects yet'), findsNothing);

      await tester.tap(find.text('Clear filter'));
      await tester.pumpAndSettle();
      expect(find.byType(ProjectListTile), findsNWidgets(2));
    });
  });

  group('start', () {
    testWidgets('the action is offered only for an unstarted project', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: '1', name: 'Fresh', status: ProjectStatus.analyzing),
        testProject(
          id: '2',
          name: 'Running',
          status: ProjectStatus.implementation,
          lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 11),
        ),
      ];
      await pump(tester);

      expect(find.text('Start'), findsOneWidget);
    });

    testWidgets('starting shows the status the server returned', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: '1', name: 'Fresh', status: ProjectStatus.analyzing),
      ];
      await pump(tester);

      await tester.tap(find.text('Start'));
      await tester.pumpAndSettle();

      expect(projects.startedIds, <String>['1']);
      // Started, so the action is gone — because the server said so.
      expect(find.text('Start'), findsNothing);
    });

    testWidgets('the button is disabled while the request is in flight', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: '1', status: ProjectStatus.analyzing),
      ];
      await pump(tester);

      final gate = Completer<void>();
      projects.gate = gate.future;

      await tester.tap(find.text('Start'));
      await tester.pump();

      final button = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Start'),
      );
      expect(button.onPressed, isNull, reason: 'a second press must not fire');

      gate.complete();
      await tester.pumpAndSettle();
      expect(projects.startedIds, hasLength(1));
    });

    testWidgets('a rejected start is shown without blanking the list', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: '1', name: 'Fresh', status: ProjectStatus.created),
      ];
      projects.mutationError = const ConflictError(
        technicalDetail: 'Project has no initialized source.',
      );
      await pump(tester);

      await tester.tap(find.text('Start'));
      await tester.pumpAndSettle();

      expect(
        find.text(
          'The resource changed in the meantime. Reload and try again.',
        ),
        findsOneWidget,
      );
      // The server's own explanation, kept for the operator.
      expect(find.text('Project has no initialized source.'), findsOneWidget);
      // The list is still there and still usable.
      expect(find.text('Fresh'), findsOneWidget);
    });
  });

  testWidgets('refresh re-reads the list', (tester) async {
    projects.projects = [testProject(name: 'Customer API')];
    await pump(tester);
    final before = projects.listCount;

    await tester.tap(find.byTooltip('Refresh'));
    await tester.pumpAndSettle();

    expect(projects.listCount, before + 1);
    expect(find.text('Customer API'), findsOneWidget);
  });

  testWidgets('the screen renders in German', (tester) async {
    projects.projects = [
      testProject(
        id: '1',
        name: 'Customer API',
        status: ProjectStatus.humanReview,
      ),
    ];
    await pump(tester);

    final context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Neues Projekt'), findsOneWidget);
    expect(find.text('Menschliche Prüfung'), findsWidgets);
    expect(find.text('Öffnen'), findsOneWidget);
    expect(find.text('Customer API'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });

  for (final entry in <String, Size>{
    'compact': TestSizes.compact,
    'tablet': TestSizes.medium,
    'desktop': TestSizes.expanded,
  }.entries) {
    testWidgets('lays out without overflow at ${entry.key} width', (
      tester,
    ) async {
      projects.projects = [
        testProject(
          id: '1',
          name: 'A project with a deliberately long name for layout testing',
          status: ProjectStatus.humanReview,
        ),
        testProject(id: '2', name: 'Mobile App', status: ProjectStatus.created),
      ];
      await pump(tester, size: entry.value);

      expect(find.byType(ProjectListTile), findsNWidgets(2));
      expect(tester.takeException(), isNull);
    });

    testWidgets('German lays out without overflow at ${entry.key} width', (
      tester,
    ) async {
      projects.projects = [
        testProject(id: '1', status: ProjectStatus.humanReview),
        testProject(id: '2', status: ProjectStatus.created),
      ];
      await pump(tester, size: entry.value);

      final context = tester.element(find.byType(Scaffold).first);
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });
  }

  testWidgets(
    'column captions appear only where rows are laid out in columns',
    (tester) async {
      projects.projects = [testProject()];
      await pump(tester, size: TestSizes.expanded);
      expect(find.text('Actions'), findsOneWidget);
    },
  );

  testWidgets('the compact layout drops the column captions', (tester) async {
    projects.projects = [testProject()];
    await pump(tester, size: TestSizes.compact);
    expect(find.text('Actions'), findsNothing);
    expect(find.byType(ProjectListTile), findsOneWidget);
  });
}
