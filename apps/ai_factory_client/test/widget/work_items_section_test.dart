import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:ai_factory_client/presentation/widgets/work_item_dependency_tree.dart';
import 'package:ai_factory_client/presentation/widgets/work_item_tile.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// The WorkItems + Dependencies section, rendered inside the real Project
/// Detail route with real translations — Workstream AA.
void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

  late FakeProjectRepository projects;
  late FakeWorkItemRepository workItems;

  setUp(() async {
    await registerTestServices();
    projects = FakeProjectRepository(
      projects: [testProject(id: projectId, status: ProjectStatus.testing)],
    );
    workItems = FakeWorkItemRepository();
    Get.put<ProjectRepository>(projects);
    Get.put<WorkItemRepository>(workItems);
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    Get.put<ApprovalRepository>(FakeApprovalRepository());
  });

  Future<void> open(WidgetTester tester, {Size size = TestSizes.expanded}) =>
      pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        size: size,
      );

  /// Scrolls Project Detail's `ListView` until [finder] is built.
  ///
  /// `ListView(children: …)` still mounts its children lazily by viewport
  /// position — a widget below the fold does not exist in the element tree
  /// until it is scrolled near enough, so `find` (and `ensureVisible`, which
  /// needs an existing element to scroll *to*) cannot see it until then. This
  /// mirrors `revealInDetail` in `integration_test/project_detail_flow_test.dart`.
  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isNotEmpty) return;
    final list = find.byType(ListView).first;
    for (var attempt = 0; attempt < 10; attempt++) {
      await tester.drag(list, const Offset(0, -600));
      await tester.pumpAndSettle();
      if (finder.evaluate().isNotEmpty) return;
    }
  }

  group('no work items', () {
    testWidgets('shows the localized empty state', (tester) async {
      await open(tester);
      await reveal(tester, find.text('No work items'));
      expect(find.text('No work items'), findsOneWidget);
      expect(
        find.text('This project does not currently expose any work items.'),
        findsOneWidget,
      );
    });
  });

  group('no dependency plan (flat list)', () {
    testWidgets('lists work items and states there is no plan', (tester) async {
      workItems.workItems = [
        testWorkItem(id: 'w1', projectId: projectId, title: 'Analysis'),
        testWorkItem(id: 'w2', projectId: projectId, title: 'Build'),
      ];
      await open(tester);
      await reveal(tester, find.text('Work items'));

      expect(find.text('Work items'), findsOneWidget);
      expect(find.text('No dependency plan'), findsOneWidget);
      expect(find.byType(WorkItemTile), findsNWidgets(2));
      expect(find.text('Analysis'), findsOneWidget);
      expect(find.text('Build'), findsOneWidget);
      expect(
        find.text('No dependency plan exists for this project'),
        findsOneWidget,
      );
    });
  });

  group('a plan exists', () {
    void stagePlan() {
      final w1 = testWorkItem(
        id: 'w1',
        projectId: projectId,
        title: 'Analysis',
      );
      final w2 = testWorkItem(
        id: 'w2',
        projectId: projectId,
        // Deliberately not "Implementation": that string also appears as a
        // `ProjectStatus` label in the lifecycle track above this section,
        // and a title matching it would make `find.text` ambiguous.
        title: 'Build the REST endpoint',
        attemptCount: 2,
        repairAttemptCount: 1,
        status: WorkItemStatus.running,
      );
      projects.planResult = ProjectPlan(
        projectId: projectId,
        planHash: 'h1',
        nodes: [
          testWorkItemNode(workItem: w1, planKey: 'T01'),
          testWorkItemNode(
            workItem: w2,
            planKey: 'T02',
            dependsOnPlanKeys: ['T01'],
            dependsOnWorkItemIds: ['w1'],
          ),
        ],
      );
    }

    testWidgets('renders the dependency tree', (tester) async {
      stagePlan();
      await open(tester);
      await reveal(tester, find.byType(WorkItemDependencyTree));

      expect(find.byType(WorkItemDependencyTree), findsOneWidget);
      expect(find.text('T01'), findsWidgets);
      expect(find.text('T02'), findsWidgets);
    });

    testWidgets('selecting a list row shows its detail panel', (tester) async {
      stagePlan();
      workItems.readinessById['w2'] = TaskReadiness.running;
      await open(tester);
      await reveal(tester, find.text('Select a work item to see its detail.'));

      expect(
        find.text('Select a work item to see its detail.'),
        findsOneWidget,
      );

      // The section sits below the fold of Project Detail's `ListView` at
      // this window size, so the row has to be scrolled into view before a
      // tap at its coordinates can land on it.
      final target = find
          .descendant(
            of: find.byType(WorkItemTile),
            matching: find.text('Build the REST endpoint'),
          )
          .first;
      await reveal(tester, target);
      // `reveal` only guarantees the row is mounted (within the sliver's
      // cache extent), not that it is actually inside the visible viewport —
      // `ensureVisible` does the precise scroll once the element exists.
      await tester.ensureVisible(target);
      await tester.pumpAndSettle();
      await tester.tap(target);
      await tester.pumpAndSettle();

      expect(Get.find<WorkItemsController>().selectedWorkItemId.value, 'w2');
      expect(find.text('Select a work item to see its detail.'), findsNothing);
      // The detail panel's dependency chip resolves the id to a title.
      expect(find.text('Analysis'), findsWidgets);
    });

    testWidgets('German locale renders translated section titles', (
      tester,
    ) async {
      stagePlan();
      await open(tester);

      final context = tester.element(find.byType(Scaffold).first);
      final localeService = Get.find<LocaleService>();
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      // German labels run longer than English ones throughout this app, so
      // the section can end up further below the fold after the switch than
      // it was before — reveal it again rather than assuming its position.
      await reveal(tester, find.text('Arbeitspakete'));
      expect(find.text('Arbeitspakete'), findsWidgets);
      expect(find.text('Abhängigkeiten'), findsOneWidget);
      expectNoUntranslatedKeys(tester);
    });
  });
}
