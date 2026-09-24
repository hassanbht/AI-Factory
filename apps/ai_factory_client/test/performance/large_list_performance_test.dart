import 'dart:convert';
import 'dart:io';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/models/project_list_filter.dart';
import 'package:ai_factory_client/presentation/widgets/agent_run_tile.dart';
import 'package:ai_factory_client/presentation/widgets/agent_runs_section.dart';
import 'package:ai_factory_client/presentation/widgets/project_list_tile.dart';
import 'package:ai_factory_client/presentation/widgets/work_item_dependency_tree.dart';
import 'package:ai_factory_client/presentation/widgets/work_item_tile.dart';
import 'package:ai_factory_client/presentation/widgets/work_items_section.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// Large-list benchmarks on synthetic, deterministic data.
///
/// The backend does not produce these volumes naturally; the data is
/// synthetic **rendering** data built with the same domain constructors the
/// mappers use — it is never presented as an API contract.
///
/// Measured in a widget test (debug JIT, software rendering): absolute times
/// are an upper bound on a release build, so the budgets are generous and
/// the assertions that matter are structural — how many rows exist, how the
/// cost grows with N, how often the repository is called. Results are
/// written to `build/perf/large_lists.json` (git-ignored) and printed.
void main() {
  setUpAll(initializeTestLocalization);

  final results = <String, Object?>{};
  tearDownAll(() {
    final file = File('build/perf/large_lists.json')
      ..createSync(recursive: true)
      ..writeAsStringSync(const JsonEncoder.withIndent('  ').convert(results));
    // ignore: avoid_print
    print('Large-list results:\n${file.readAsStringSync()}');
  });

  setUp(() async {
    pinCatalogClock();
    await registerTestServices();
  });

  final created = DateTime.utc(2026, 9, 20);
  final statuses = ProjectStatus.values
      .where((s) => s != ProjectStatus.unknown)
      .toList();

  List<Project> projects(int n) => [
    for (var i = 0; i < n; i++)
      testProject(
        id: 'p$i',
        name: 'Project ${i.toString().padLeft(5, '0')}',
        status: statuses[i % statuses.length],
        createdAtUtc: created.subtract(Duration(minutes: i)),
      ),
  ];

  group('Projects list', () {
    final timings = <int, int>{};

    for (final n in const [100, 500, 1000, 5000]) {
      testWidgets('$n projects: virtualized, one request, bounded build', (
        tester,
      ) async {
        registerCatalog(CatalogScreen.projects);
        final repo = FakeProjectRepository(projects: projects(n));
        Get.delete<ProjectRepository>(force: true);
        Get.put<ProjectRepository>(repo);

        final watch = Stopwatch()..start();
        await pumpScreen(
          tester,
          initialRoute: AppRoutes.projects,
          getPages: AppRoutes.pages,
          size: const Size(1280, 900),
        );
        final firstFrameMs = watch.elapsedMilliseconds;

        final built = find.byType(ProjectListTile).evaluate().length;
        // Only what fits in the viewport (plus cache extent) is built.
        expect(built, lessThan(40), reason: 'built $built of $n rows');

        // Scroll far: each drag builds a new window of rows.
        final list = find.byType(Scrollable).last;
        final scroll = Stopwatch()..start();
        for (var i = 0; i < 20; i++) {
          await tester.drag(list, const Offset(0, -2000));
          await tester.pump();
        }
        final scrollMs = scroll.elapsedMilliseconds;
        final builtAfterScroll = find.byType(ProjectListTile).evaluate().length;
        expect(builtAfterScroll, lessThan(40));

        // Scrolling never re-reads the list.
        expect(repo.listCount, 1);
        timings[n] = firstFrameMs;
        results['projects_$n'] = {
          'firstFrameMs': firstFrameMs,
          'rowsBuilt': built,
          'rowsBuiltAfterScroll': builtAfterScroll,
          'scroll20DragsMs': scrollMs,
          'repositoryListCalls': repo.listCount,
        };
        expect(firstFrameMs, lessThan(5000));
      });
    }

    test('first-frame cost does not grow like O(n²)', () {
      // 10× the projects must cost far less than 100× the time; generous
      // noise allowance for a debug VM.
      final small = timings[500];
      final large = timings[5000];
      if (small == null || large == null) return; // a sibling failed first
      expect(large, lessThan(small * 20 + 500));
    });
  });

  group('project list filter and sort', () {
    for (final n in const [1000, 5000]) {
      test('$n projects filter and sort quickly', () {
        final list = projects(n);
        final watch = Stopwatch()..start();
        for (final status in ProjectStatusFilter.values) {
          final filter = ProjectListFilter(status: status, query: 'project 0');
          ProjectSort.apply(list.where(filter.matches));
        }
        final ms = watch.elapsedMilliseconds;
        results['filter_and_sort_all_statuses_$n'] = {'ms': ms};
        expect(ms, lessThan(2000));
      });
    }
  });

  group('WorkItems on Project Detail', () {
    for (final n in const [100, 500, 1000]) {
      testWidgets('$n work items with a linear plan', (tester) async {
        registerCatalog(CatalogScreen.projectDetail);
        const projectId = ScreenCatalog.implementationId;
        final items = [
          for (var i = 0; i < n; i++)
            testWorkItem(
              id: 'w$i',
              projectId: projectId,
              title: 'Work item $i',
              status: WorkItemStatus.values[i % 5],
            ),
        ];
        final plan = ProjectPlan(
          projectId: projectId,
          planHash: 'h',
          nodes: [
            for (var i = 0; i < n; i++)
              testWorkItemNode(
                workItem: items[i],
                planKey: 'T$i',
                dependsOnPlanKeys: i == 0 ? const [] : ['T${i - 1}'],
              ),
          ],
        );
        final workItems = FakeWorkItemRepository(workItems: items);
        Get.delete<WorkItemRepository>(force: true);
        Get.put<WorkItemRepository>(workItems);
        final projectsRepo =
            Get.find<ProjectRepository>() as FakeProjectRepository
              ..planResult = plan;

        final watch = Stopwatch()..start();
        await pumpScreen(
          tester,
          initialRoute: AppRoutes.projectDetail(projectId),
          getPages: AppRoutes.pages,
          size: const Size(1280, 900),
        );
        final pageMs = watch.elapsedMilliseconds;

        // The sections sit below the fold; scrolling to them is what an
        // operator does, and it is where their build cost is paid.
        final reveal = Stopwatch()..start();
        await revealInPage(tester, find.byType(WorkItemsSection));
        final revealMs = reveal.elapsedMilliseconds;
        final ms = pageMs + revealMs;
        final built = find.byType(WorkItemTile).evaluate().length;
        results['work_items_$n'] = {
          'pageMs': pageMs,
          'revealSectionMs': revealMs,
          'tilesBuilt': built,
          'treeRowsBuilt': find
              .descendant(
                of: find.byType(WorkItemDependencyTree),
                matching: find.byType(InkWell),
              )
              .evaluate()
              .length,
          'getPlanCalls': projectsRepo.calls
              .where((c) => c.startsWith('getPlan:'))
              .length,
        };
        // One plan read (which carries the work items), no per-item reads.
        expect(
          projectsRepo.calls.where((c) => c.startsWith('getPlan:')).length,
          1,
        );
        expect(workItems.listByProjectCount, 0);
        expect(workItems.getReadinessCount, 0);
        expect(ms, lessThan(15000));
      });
    }
  });

  group('AgentRuns on Project Detail', () {
    for (final n in const [100, 500, 1000]) {
      testWidgets('$n agent runs render summary rows only', (tester) async {
        registerCatalog(CatalogScreen.projectDetail);
        const projectId = ScreenCatalog.implementationId;
        // Every run carries a large output and structured result: the list
        // must stay summary-only and never render them per row.
        final bulky = 'x' * 20000;
        final runs = [
          for (var i = 0; i < n; i++)
            testAgentRun(
              id: 'r$i',
              projectId: projectId,
              workItemId: 'w3-$projectId',
              agentDefinitionId: 'def-impl',
              status: AgentRunStatus.values[i % 7],
              output: bulky,
              structuredResult: StructuredAgentResult(
                version: 1,
                status: StructuredResultStatus.succeeded,
                rawStatus: 'Succeeded',
                summary: bulky,
                warnings: List.filled(200, 'bulky warning'),
                errors: const [],
                artifacts: List.filled(200, 'artifact.txt'),
              ),
            ),
        ];
        final agentRuns = FakeAgentRunRepository(
          runs: runs,
          definitions: ScreenCatalog.data.definitions(),
        );
        Get.delete<AgentRunRepository>(force: true);
        Get.put<AgentRunRepository>(agentRuns);

        final watch = Stopwatch()..start();
        await pumpScreen(
          tester,
          initialRoute: AppRoutes.projectDetail(projectId),
          getPages: AppRoutes.pages,
          size: const Size(1280, 900),
        );
        await revealInPage(tester, find.byType(AgentRunsSection));
        final ms = watch.elapsedMilliseconds;

        // No row renders the bulky payload.
        expect(find.text(bulky), findsNothing);
        expect(find.textContaining('bulky warning'), findsNothing);
        final built = find.byType(AgentRunTile).evaluate().length;
        results['agent_runs_$n'] = {
          'pageAndRevealMs': ms,
          'tilesBuilt': built,
          'listCalls': agentRuns.listQueries.length,
          'requestedPageSize': agentRuns.listQueries.first.pageSize,
        };
        // The API pages runs; the screen asks for one page, never all.
        expect(agentRuns.listQueries.first.pageSize, 100);
        expect(ms, lessThan(15000));
      });
    }
  });

  group('polling does not rebuild what did not change', () {
    testWidgets('an unchanged cycle leaves every row untouched; a changed one '
        'rebuilds', (tester) async {
      registerCatalog(CatalogScreen.projectDetail);
      await pumpScreen(
        tester,
        initialRoute: CatalogScreen.projectDetail.route,
        getPages: AppRoutes.pages,
        size: const Size(1280, 3000),
      );
      final live = Get.find<ProjectDetailController>().live;
      WorkItemTile tile() =>
          tester.widget<WorkItemTile>(find.byType(WorkItemTile).first);
      AgentRunTile run() =>
          tester.widget<AgentRunTile>(find.byType(AgentRunTile).first);

      final tileBefore = tile();
      final runBefore = run();
      await live.refreshNow(trigger: PollTrigger.tick);
      await tester.pumpAndSettle();
      expect(identical(tile(), tileBefore), isTrue, reason: 'rebuilt rows');
      expect(identical(run(), runBefore), isTrue, reason: 'rebuilt runs');

      // A real change still reaches the screen.
      final repo = Get.find<WorkItemRepository>() as FakeWorkItemRepository;
      final projects = Get.find<ProjectRepository>() as FakeProjectRepository;
      final plan = projects.planResult!;
      projects.planResult = ProjectPlan(
        projectId: plan.projectId,
        planHash: plan.planHash,
        nodes: [
          testWorkItemNode(
            workItem: testWorkItem(
              id: plan.nodes.first.workItem.id,
              projectId: plan.projectId,
              title: 'Renamed by the server',
            ),
            planKey: plan.nodes.first.planKey,
          ),
          ...plan.nodes.skip(1),
        ],
      );
      await live.refreshNow(trigger: PollTrigger.tick);
      await tester.pumpAndSettle();
      expect(find.text('Renamed by the server'), findsWidgets);
      expect(repo.listByProjectCount, 0);
    });
  });
}
