import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/widgets/agent_run_detail_panel.dart';
import 'package:ai_factory_client/presentation/widgets/agent_run_tile.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// The AgentRuns section, rendered inside the real Project Detail route with
/// real translations — Workstream AB.
void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

  late FakeAgentRunRepository agentRuns;

  setUp(() async {
    await registerTestServices();
    Get.put<ProjectRepository>(
      FakeProjectRepository(
        projects: [testProject(id: projectId, status: ProjectStatus.testing)],
      ),
    );
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    agentRuns = FakeAgentRunRepository(definitions: []);
    Get.put<AgentRunRepository>(agentRuns);
    Get.put<ApprovalRepository>(FakeApprovalRepository());
  });

  Future<void> open(WidgetTester tester, {Size size = TestSizes.expanded}) =>
      pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
        size: size,
      );

  /// Scrolls Project Detail's `ListView` until [finder] is built — the same
  /// lazy-mounting workaround `work_items_section_test.dart` documents.
  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isNotEmpty) return;
    final list = find.byType(ListView).first;
    for (var attempt = 0; attempt < 12; attempt++) {
      await tester.drag(list, const Offset(0, -600));
      await tester.pumpAndSettle();
      if (finder.evaluate().isNotEmpty) return;
    }
  }

  group('no agent runs', () {
    testWidgets('shows the localized empty state', (tester) async {
      await open(tester);
      await reveal(tester, find.text('No agent runs'));
      expect(find.text('No agent runs'), findsOneWidget);
      expect(
        find.text('No agent has run for this project yet.'),
        findsOneWidget,
      );
    });
  });

  group('runs exist', () {
    void stageRuns() {
      agentRuns.definitions = [
        testAgentDefinition(
          id: 'def1',
          name: 'Implementation Agent',
          agentType: WorkItemType.implementation,
        ),
      ];
      agentRuns.runs = [
        testAgentRun(
          id: 'r1',
          agentDefinitionId: 'def1',
          workItemId: 'w1',
          status: AgentRunStatus.succeeded,
          attempt: 1,
          startedAtUtc: DateTime.utc(2026, 9, 19, 10),
          completedAtUtc: DateTime.utc(2026, 9, 19, 10, 2),
          duration: const Duration(minutes: 2),
          structuredResult: const StructuredAgentResult(
            version: 1,
            status: StructuredResultStatus.succeeded,
            rawStatus: 'Succeeded',
            summary: 'Endpoint implemented.',
            warnings: <String>[],
            errors: <String>[],
            artifacts: <String>['lib/api/endpoint.dart'],
          ),
        ),
        testAgentRun(
          id: 'r2',
          agentDefinitionId: 'def1',
          workItemId: 'w2',
          status: AgentRunStatus.failed,
          attempt: 2,
          failureCategory: FailureCategory.build,
          errorMessage: 'Compilation failed.',
        ),
      ];
    }

    testWidgets('lists the runs with their agent type and status', (
      tester,
    ) async {
      stageRuns();
      await open(tester);
      await reveal(tester, find.text('Agent runs'));

      expect(find.byType(AgentRunTile), findsNWidgets(2));
      expect(find.text('Implementation Agent'), findsWidgets);
      expect(find.text('Succeeded'), findsWidgets);
      expect(find.text('Failed'), findsWidgets);
    });

    testWidgets('selecting a run shows its detail panel', (tester) async {
      stageRuns();
      await open(tester);
      await reveal(tester, find.text('Select an agent run to see its detail.'));
      expect(
        find.text('Select an agent run to see its detail.'),
        findsOneWidget,
      );

      final target = find
          .descendant(
            of: find.byType(AgentRunTile),
            matching: find.text('Implementation Agent'),
          )
          .first;
      await reveal(tester, target);
      await tester.ensureVisible(target);
      await tester.pumpAndSettle();
      await tester.tap(target);
      await tester.pumpAndSettle();

      expect(Get.find<AgentRunsController>().selectedAgentRunId.value, 'r1');
      expect(find.byType(AgentRunDetailPanel), findsOneWidget);
      expect(find.text('Endpoint implemented.'), findsOneWidget);
      expect(find.text('lib/api/endpoint.dart'), findsOneWidget);
    });

    testWidgets('a failed run shows its error and failure category', (
      tester,
    ) async {
      stageRuns();
      await open(tester);
      await reveal(tester, find.text('Select an agent run to see its detail.'));

      final lastTile = find.byType(AgentRunTile).last;
      await reveal(tester, lastTile);
      await tester.ensureVisible(lastTile);
      await tester.pumpAndSettle();
      await tester.tap(lastTile);
      await tester.pumpAndSettle();

      expect(find.text('Compilation failed.'), findsOneWidget);
      expect(find.text('Build'), findsOneWidget);
    });

    testWidgets('states that model and provider telemetry is unavailable', (
      tester,
    ) async {
      stageRuns();
      await open(tester);

      final target = find
          .descendant(
            of: find.byType(AgentRunTile),
            matching: find.text('Implementation Agent'),
          )
          .first;
      await reveal(tester, target);
      await tester.ensureVisible(target);
      await tester.pumpAndSettle();
      await tester.tap(target);
      await tester.pumpAndSettle();

      await reveal(tester, find.text('Execution telemetry'));
      expect(find.text('Execution telemetry'), findsOneWidget);
      expect(
        find.textContaining('not returned by this endpoint'),
        findsOneWidget,
      );
    });

    testWidgets('filtering by status re-queries the server', (tester) async {
      stageRuns();
      await open(tester);
      final dropdown = find.text('All statuses');
      await reveal(tester, dropdown);
      await tester.ensureVisible(dropdown);
      await tester.pumpAndSettle();

      await tester.tap(dropdown);
      await tester.pumpAndSettle();
      final failedOption = find.text('Failed').last;
      await tester.ensureVisible(failedOption);
      await tester.pumpAndSettle();
      await tester.tap(failedOption);
      await tester.pumpAndSettle();

      expect(agentRuns.listQueries.last.status, AgentRunStatus.failed);
    });

    testWidgets('German locale renders translated section titles', (
      tester,
    ) async {
      stageRuns();
      await open(tester);

      final context = tester.element(find.byType(Scaffold).first);
      final localeService = Get.find<LocaleService>();
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      await reveal(tester, find.text('Agentenläufe'));
      expect(find.text('Agentenläufe'), findsWidgets);
      expectNoUntranslatedKeys(tester);
    });
  });
}
