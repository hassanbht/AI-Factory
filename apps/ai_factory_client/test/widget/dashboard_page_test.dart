import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/core/responsive/breakpoints.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:ai_factory_client/presentation/pages/dashboard_page.dart';
import 'package:ai_factory_client/presentation/widgets/panels.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

void main() {
  setUpAll(initializeTestLocalization);

  late FakeProjectRepository projects;
  late FakeOperationsRepository operations;
  late FakeDiagnosticsRepository diagnostics;
  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
    projects = FakeProjectRepository();
    operations = FakeOperationsRepository();
    diagnostics = FakeDiagnosticsRepository();
  });

  DashboardController stage() {
    final controller = DashboardController(
      projects: projects,
      operations: operations,
      diagnostics: diagnostics,
    );
    Get.put<DashboardController>(controller);
    return controller;
  }

  Future<DashboardController> pump(
    WidgetTester tester, {
    Size size = TestSizes.expanded,
  }) async {
    final controller = stage();
    await pumpScreen(tester, home: const DashboardPage(), size: size);
    return controller;
  }

  testWidgets('shows a loading state before the data arrives', (tester) async {
    final gate = Completer<void>();
    projects.gate = gate.future;
    stage();

    Get.testMode = true;
    await setSurfaceSize(tester, TestSizes.expanded);
    await tester.pumpWidget(const TestAppScope(home: DashboardPage()));
    await tester.pump();

    // Never a blank frame: the operator sees that something is happening.
    expect(find.byType(CircularProgressIndicator), findsWidgets);

    gate.complete();
    await tester.pumpAndSettle();
  });

  testWidgets('renders every band once loaded', (tester) async {
    projects.projects = [
      testProject(id: '1', name: 'Customer API', status: ProjectStatus.implementation),
      testProject(id: '2', name: 'Mobile App', status: ProjectStatus.humanReview),
    ];
    await pump(tester);

    expect(find.text('Needs attention'), findsOneWidget);
    expect(find.text('Projects'), findsWidgets);
    expect(find.text('Recently updated'), findsOneWidget);
    expect(find.text('Platform'), findsOneWidget);

    // Counted from the project list, and the count is the real one.
    expect(find.text('Total'), findsOneWidget);
    expect(find.widgetWithText(MetricTile, '2'), findsWidgets);

    // The project awaiting a decision is the one surfaced.
    expect(find.text('Mobile App'), findsWidgets);
    expectNoUntranslatedKeys(tester);
  });

  testWidgets('says nothing needs attention when nothing does', (tester) async {
    projects.projects = [
      testProject(id: '1', status: ProjectStatus.implementation),
    ];
    await pump(tester);

    expect(find.text('Nothing needs your attention.'), findsOneWidget);
  });

  testWidgets('an empty factory renders the bands without inventing data', (
    tester,
  ) async {
    await pump(tester);

    expect(find.text('Nothing needs your attention.'), findsOneWidget);
    expect(find.text('No projects yet.'), findsOneWidget);
    expect(find.widgetWithText(MetricTile, '0'), findsWidgets);
  });

  testWidgets('a failed project list shows an error with retry', (tester) async {
    projects.listError = const NetworkError();
    final controller = await pump(tester);

    expect(find.text('Something went wrong'), findsOneWidget);
    // The localized message, never the raw transport failure.
    expect(find.textContaining('No connection'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);

    projects.listError = null;
    projects.projects = [testProject()];
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();

    expect(find.text('Something went wrong'), findsNothing);
    expect(controller.state.value.dataOrNull, isNotNull);
  });

  testWidgets('the refresh action re-reads without blanking the screen', (
    tester,
  ) async {
    projects.projects = [testProject(name: 'Customer API')];
    await pump(tester);
    final before = projects.listCount;

    await tester.tap(find.byTooltip('Refresh'));
    await tester.pump();

    // Content stays visible while the refresh runs.
    expect(find.text('Customer API'), findsWidgets);

    await tester.pumpAndSettle();
    expect(projects.listCount, before + 1);
  });

  testWidgets('platform health renders from the health probes', (tester) async {
    operations.health = testHealth(alive: true, ready: false);
    await pump(tester);

    expect(find.text('Reachable'), findsOneWidget);
    expect(find.text('Not ready'), findsOneWidget);
  });

  testWidgets('an unreadable source renders as not available, not as zero', (
    tester,
  ) async {
    operations.schedulerError = const ServerError();
    await pump(tester);

    expect(find.text('Some information could not be loaded'), findsOneWidget);
    expect(find.text('Not available'), findsWidgets);
  });

  testWidgets('agent metrics say an operational key is required on 401', (
    tester,
  ) async {
    diagnostics.metricsError = const AuthenticationError();
    await pump(tester);

    expect(
      find.textContaining('require an operational key'),
      findsOneWidget,
    );
    // Not presented as a failure the operator should retry.
    expect(find.text('Some information could not be loaded'), findsNothing);
  });

  testWidgets('worker health is explicitly declared unavailable', (tester) async {
    await pump(tester);

    // Scheduler activity must never be dressed up as worker health.
    expect(find.text('Worker health'), findsOneWidget);
    expect(
      find.textContaining('no worker health endpoint'),
      findsOneWidget,
    );
  });

  testWidgets('the whole dashboard renders in German', (tester) async {
    projects.projects = [
      testProject(id: '2', name: 'Mobile App', status: ProjectStatus.humanReview),
    ];
    await pump(tester);

    final context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Erfordert Aufmerksamkeit'), findsOneWidget);
    expect(find.text('Plattform'), findsOneWidget);
    expect(find.text('Zuletzt aktualisiert'), findsOneWidget);
    // The status label is localized; the project name is operator data and
    // stays exactly as the API returned it.
    expect(find.text('Menschliche Prüfung'), findsWidgets);
    expect(find.text('Mobile App'), findsWidgets);
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
        testProject(id: '1', name: 'Customer API', status: ProjectStatus.humanReview),
        testProject(id: '2', name: 'Mobile App', status: ProjectStatus.failed),
      ];
      await pump(tester, size: entry.value);

      expect(tester.takeException(), isNull);
      expect(find.byType(MetricTile), findsWidgets);
    });

    testWidgets('German lays out without overflow at ${entry.key} width', (
      tester,
    ) async {
      projects.projects = [testProject(status: ProjectStatus.humanReview)];
      await pump(tester, size: entry.value);

      final context = tester.element(find.byType(Scaffold).first);
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });
  }

  test('tiles use one column when compact and four when expanded', () {
    // The same children, arranged differently — not two implementations.
    expect(ResponsiveTileGrid.columnsFor(WindowSizeClass.compact), 1);
    expect(ResponsiveTileGrid.columnsFor(WindowSizeClass.medium), 2);
    expect(ResponsiveTileGrid.columnsFor(WindowSizeClass.expanded), 4);
  });
}
