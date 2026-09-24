import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/core/polling/app_activity.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:ai_factory_client/presentation/controllers/diagnostics_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operations_controller.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/controllers/projects_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// Navigation and state restoration across the whole application
/// (Workstreams AJ and AK), through the production routing table.
///
/// At every step: exactly the controllers the visible routes need, exactly
/// one live coordinator per mounted live screen (a covered screen's is
/// paused, not duplicated), the locale kept, and the project context kept.
void main() {
  setUpAll(initializeTestLocalization);

  late LocaleService locale;
  late int baseline;

  setUp(() async {
    pinCatalogClock();
    locale = await registerTestServices();
    registerCatalog(CatalogScreen.projectDetail);
    baseline = PollingCoordinator.debugLiveCount;
  });

  int live() => PollingCoordinator.debugLiveCount - baseline;

  Future<void> go(WidgetTester tester, String route) async {
    Get.offNamed<void>(route);
    await tester.pumpAndSettle();
  }

  Future<void> push(WidgetTester tester, String route) async {
    Get.toNamed<void>(route);
    await tester.pumpAndSettle();
  }

  Future<void> back(WidgetTester tester) async {
    Get.back<void>();
    await tester.pumpAndSettle();
  }

  const detailId = ScreenCatalog.implementationId;

  testWidgets('Dashboard → Projects → Detail → WorkItem → AgentRun → '
      'Diagnostics → back → back → Operations → Settings → Dashboard', (
    tester,
  ) async {
    await pumpScreen(
      tester,
      initialRoute: AppRoutes.dashboard,
      getPages: AppRoutes.pages,
    );
    expect(Get.isRegistered<DashboardController>(), isTrue);
    expect(live(), 1);

    await go(tester, AppRoutes.projects);
    expect(Get.isRegistered<DashboardController>(), isFalse);
    expect(Get.isRegistered<ProjectsController>(), isTrue);
    expect(live(), 1, reason: 'the dashboard poller went with its route');

    await tester.tap(find.text('Customer API').first);
    await tester.pumpAndSettle();
    expect(Get.currentRoute, AppRoutes.projectDetail(detailId));
    // Projects stays mounted beneath, paused — not a second active poller.
    expect(live(), 2);
    expect(Get.find<ProjectsController>().live.isPaused, isTrue);

    final workItems = Get.find<WorkItemsController>();
    final agentRuns = Get.find<AgentRunsController>();
    workItems.selectWorkItem('w3-$detailId');
    agentRuns.selectAgentRun('r2-$detailId');
    await tester.pumpAndSettle();

    await push(tester, AppRoutes.projectDiagnostics(detailId));
    expect(Get.find<DiagnosticsController>().projectId, detailId);
    expect(Get.find<ProjectDetailController>().live.isPaused, isTrue);
    expect(live(), 3);

    await back(tester);
    expect(Get.isRegistered<DiagnosticsController>(), isFalse);
    expect(live(), 2);
    // State survived the round trip: same controllers, same selection,
    // polling resumed.
    expect(identical(Get.find<WorkItemsController>(), workItems), isTrue);
    expect(workItems.selectedWorkItemId.value, 'w3-$detailId');
    expect(agentRuns.selectedAgentRunId.value, 'r2-$detailId');
    expect(Get.find<ProjectDetailController>().live.isPaused, isFalse);

    await back(tester);
    expect(Get.currentRoute, AppRoutes.projects);
    expect(Get.isRegistered<ProjectDetailController>(), isFalse);
    expect(Get.isRegistered<WorkItemsController>(), isFalse);
    expect(Get.isRegistered<AgentRunsController>(), isFalse);
    expect(live(), 1);
    expect(Get.find<ProjectsController>().live.isPaused, isFalse);

    await go(tester, AppRoutes.operations);
    expect(Get.isRegistered<ProjectsController>(), isFalse);
    expect(Get.isRegistered<OperationsController>(), isTrue);
    expect(live(), 1);

    await go(tester, AppRoutes.settings);
    expect(Get.isRegistered<OperationsController>(), isFalse);
    expect(live(), 0, reason: 'Settings does not poll');

    // The language chosen here follows the operator everywhere.
    await locale.apply(
      tester.element(find.byType(Scaffold).first),
      LocalePreference.german,
    );
    await tester.pumpAndSettle();
    await go(tester, AppRoutes.dashboard);
    expect(find.text('Projekte'), findsWidgets);
    expect(find.text('Projects'), findsNothing);
    expect(live(), 1);
  });

  testWidgets('open → back → open, 25 times: no duplicate controllers or '
      'pollers', (tester) async {
    await pumpScreen(
      tester,
      initialRoute: AppRoutes.projects,
      getPages: AppRoutes.pages,
    );
    for (var i = 0; i < 25; i++) {
      await push(tester, AppRoutes.projectDetail(detailId));
      expect(live(), 2, reason: 'round $i');
      expect(Get.isRegistered<ProjectDetailController>(), isTrue);
      await back(tester);
      expect(live(), 1, reason: 'round $i');
      expect(Get.isRegistered<ProjectDetailController>(), isFalse);
    }
  });

  testWidgets('switching project: the second detail shows the second project '
      'and nothing polls the first', (tester) async {
    final projects = Get.find<ProjectRepository>() as FakeProjectRepository;
    await pumpScreen(
      tester,
      initialRoute: AppRoutes.projects,
      getPages: AppRoutes.pages,
    );
    await push(tester, AppRoutes.projectDetail(detailId));
    await back(tester);
    await push(tester, AppRoutes.projectDetail(ScreenCatalog.reviewId));

    expect(
      Get.find<ProjectDetailController>().projectId,
      ScreenCatalog.reviewId,
    );
    expect(find.text('Billing Service'), findsWidgets);
    projects.calls.clear();
    await tester.pump(const Duration(seconds: 30));
    expect(
      projects.calls.where((c) => c == 'getById:$detailId'),
      isEmpty,
      reason: 'the first project is no longer polled',
    );
    await back(tester);
  });

  group('state restoration', () {
    testWidgets('a deep link recreates the route with its project context', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDiagnostics(detailId),
        getPages: AppRoutes.pages,
      );
      expect(Get.find<DiagnosticsController>().projectId, detailId);
      expect(live(), 1);
    });

    testWidgets('app resume refreshes at once and keeps what is on screen', (
      tester,
    ) async {
      // What AppBindings registers in the application.
      final activity = Get.put(AppActivity.fromBinding());
      addTearDown(activity.dispose);
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(detailId),
        getPages: AppRoutes.pages,
      );
      final projects = Get.find<ProjectRepository>() as FakeProjectRepository;
      Get.find<WorkItemsController>().selectWorkItem('w3-$detailId');
      await tester.pumpAndSettle();

      // The platform's own order: resumed → inactive → hidden, and back.
      for (final state in const [
        AppLifecycleState.inactive,
        AppLifecycleState.hidden,
      ]) {
        tester.binding.handleAppLifecycleStateChanged(state);
        await tester.pump();
      }
      final before = projects.getByIdCount;
      for (final state in const [
        AppLifecycleState.inactive,
        AppLifecycleState.resumed,
      ]) {
        tester.binding.handleAppLifecycleStateChanged(state);
        await tester.pump();
      }
      await tester.pumpAndSettle();

      expect(projects.getByIdCount, before + 1);
      expect(find.text('Customer API'), findsWidgets);
      expect(
        Get.find<WorkItemsController>().selectedWorkItemId.value,
        'w3-$detailId',
      );
    });

    testWidgets('a language switch keeps data and selection, without a '
        'second poller', (tester) async {
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(detailId),
        getPages: AppRoutes.pages,
      );
      Get.find<WorkItemsController>().selectWorkItem('w3-$detailId');
      await tester.pumpAndSettle();

      await locale.apply(
        tester.element(find.byType(Scaffold).first),
        LocalePreference.german,
      );
      await tester.pumpAndSettle();

      expect(find.text('Live-Aktualisierung aktiv'), findsOneWidget);
      expect(find.text('Customer API'), findsWidgets);
      expect(
        Get.find<WorkItemsController>().selectedWorkItemId.value,
        'w3-$detailId',
      );
      expect(live(), 1);
    });

    testWidgets('a temporary network failure keeps the data, says so, and '
        'recovers by itself', (tester) async {
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(detailId),
        getPages: AppRoutes.pages,
      );
      final projects = Get.find<ProjectRepository>() as FakeProjectRepository;
      final detail = Get.find<ProjectDetailController>();

      projects.getByIdError = const NetworkError();
      await detail.live.refreshNow();
      await tester.pumpAndSettle();
      expect(find.text('Customer API'), findsWidgets, reason: 'data kept');
      expect(find.textContaining('Offline'), findsOneWidget);

      projects.getByIdError = null;
      await tester.pump(const Duration(seconds: 30));
      await tester.pumpAndSettle();
      expect(find.textContaining('Offline'), findsNothing);
      expect(find.text('Live updates on'), findsOneWidget);
    });
  });
}
