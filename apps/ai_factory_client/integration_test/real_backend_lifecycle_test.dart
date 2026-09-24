import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';
import 'support/live_backend.dart';

/// Phase 19.10 real-backend matrix, lifecycle half: one project from Source
/// to Completed, on native Windows, against a real API on a throwaway
/// database.
///
///     flutter test integration_test/real_backend_lifecycle_test.dart -d windows \
///       --dart-define=AI_FACTORY_LIVE=true \
///       --dart-define=AI_FACTORY_API_BASE_URL=http://127.0.0.1:5299
///
/// Verified here against the real API:
///
/// * **Start** from the screen (`POST /start`);
/// * the **lifecycle** observed as another operator moves the project;
/// * **WorkItems** and the dependency tree from a real plan
///   (`POST /plan` by the other operator — the client never submits plans);
/// * an **AgentRun** from `GET /api/agents/runs` (queued: no Worker runs);
/// * **Human Review** reached, **approved from the screen** through the
///   confirmation dialog, and **Completed** shown;
/// * polling stops on the terminal state.
///
/// Not verified here: agent execution (it needs the Worker and a model
/// provider; not run, as the phase requires), and "Create Project" from the
/// dialog, which `operator_flow_test.dart` covers live.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(initializeTestLocalization);

  testWidgets('Source → Start → lifecycle → WorkItems → AgentRun → Human '
      'Review → Approve → Completed', (tester) async {
    final live = await LiveBackend.start();
    final project = await live.createProject('Live lifecycle 19.10');
    expect(project.status, ProjectStatus.analyzing);

    Get.testMode = true;
    await setSurfaceSize(tester, const Size(1440, 2400));
    await tester.pumpWidget(
      TestAppScope(
        startLocale: const Locale('en'),
        initialRoute: AppRoutes.projectDetail(project.id),
        getPages: AppRoutes.pages,
      ),
    );
    ProjectDetailController detail() => Get.find<ProjectDetailController>();

    await waitUntil(
      tester,
      'the project to load',
      () => detail().project != null,
    );

    // Start, from the screen.
    await waitUntil(
      tester,
      'the Start button',
      () => find.text('Start').evaluate().isNotEmpty,
    );
    await tester.tap(find.text('Start'));
    await waitUntil(
      tester,
      'the start to be recorded',
      () => detail().project?.lifecycleStartedAtUtc != null,
    );
    debugPrint('LIVE: started from the screen');

    // Another operator moves the project to Planning and submits a plan.
    await live.advance(project.id, const [
      ProjectStatus.specification,
      ProjectStatus.architecture,
      ProjectStatus.planning,
    ]);
    await live.submitPlan(project.id);
    await live.queueAgentRun(project.id);

    final workItems = Get.find<WorkItemsController>();
    final agentRuns = Get.find<AgentRunsController>();
    final seenPlan = await waitUntil(
      tester,
      'the plan and its work items',
      () => (workItems.state.value.dataOrNull?.workItems.length ?? 0) == 2,
    );
    await waitUntil(
      tester,
      'the queued agent run',
      () => (agentRuns.state.value.dataOrNull?.runs.length ?? 0) == 1,
    );
    await tester.pump();
    expect(find.text('Analyse the requirements'), findsWidgets);
    expect(find.text('Implement the endpoint'), findsWidgets);
    expect(find.text('Queued'), findsWidgets);
    debugPrint('LIVE: plan, work items and agent run seen after $seenPlan');

    // … to the human gate.
    await live.advance(project.id, const [
      ProjectStatus.implementation,
      ProjectStatus.testing,
      ProjectStatus.reviewing,
      ProjectStatus.humanReview,
    ]);
    await waitUntil(
      tester,
      'Human Review',
      () => detail().project?.status == ProjectStatus.humanReview,
    );
    await tester.pump();

    // Approval, from the screen, through the confirmation dialog.
    await tester.ensureVisible(find.text('Approve project'));
    await tester.pump();
    await tester.tap(find.text('Approve project'));
    await waitUntil(
      tester,
      'the confirmation dialog',
      () => find.byType(AlertDialog).evaluate().isNotEmpty,
    );
    await tester.tap(
      find.descendant(
        of: find.byType(AlertDialog),
        matching: find.text('Approve'),
      ),
    );
    final seenCompleted = await waitUntil(
      tester,
      'Completed',
      () => detail().project?.status == ProjectStatus.completed,
    );
    debugPrint(
      'LIVE: approved from the screen; Completed after $seenCompleted',
    );

    final server = await live.otherProjects.getById(project.id);
    expect(server.status, ProjectStatus.completed);

    await waitUntil(
      tester,
      'polling to stop',
      () => !detail().live.isInFlight && !detail().live.hasScheduledTick,
    );
    debugPrint('LIVE: polling stopped on Completed');

    await tester.pumpWidget(const SizedBox.shrink());
    await Get.deleteAll(force: true);
  }, skip: !LiveBackend.enabled);
}
