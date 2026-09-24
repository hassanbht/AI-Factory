import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/diagnostics_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';
import 'support/live_backend.dart';

/// Phase 19.10 real-backend matrix: Project Diagnostics for a real project,
/// on native Windows, against a real API on a throwaway database.
///
///     flutter test integration_test/real_backend_diagnostics_test.dart -d windows \
///       --dart-define=AI_FACTORY_LIVE=true \
///       --dart-define=AI_FACTORY_API_BASE_URL=http://127.0.0.1:5299
///
/// The project gets a real plan and a queued agent run from another
/// operator, so the diagnostics carry real work-item counts; then it is
/// approved elsewhere and the screen stops polling.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(initializeTestLocalization);

  testWidgets('Diagnostics follow a real project and stop when it completes', (
    tester,
  ) async {
    final live = await LiveBackend.start();
    final project = await live.createProject('Live diagnostics 19.10');
    await live.advance(project.id, const [
      ProjectStatus.specification,
      ProjectStatus.architecture,
      ProjectStatus.planning,
    ]);
    await live.submitPlan(project.id);
    await live.queueAgentRun(project.id);

    Get.testMode = true;
    await setSurfaceSize(tester, const Size(1440, 2000));
    await tester.pumpWidget(
      TestAppScope(
        startLocale: const Locale('en'),
        initialRoute: AppRoutes.projectDiagnostics(project.id),
        getPages: AppRoutes.pages,
      ),
    );
    await waitUntil(
      tester,
      'the route to bind its controller',
      () => Get.isRegistered<DiagnosticsController>(),
    );
    final diagnostics = Get.find<DiagnosticsController>();

    await waitUntil(
      tester,
      'the diagnostics to load',
      () => diagnostics.state.value.dataOrNull != null,
    );
    final data = diagnostics.state.value.dataOrNull!;
    expect(data.projectStatus, ProjectStatus.planning);
    expect(data.workItemStatusCounts.values.fold<int>(0, (a, b) => a + b), 2);
    // The backend counts only *Running* runs as active; a queued run (no
    // Worker takes it here) is not one. Recorded as found, not assumed.
    expect(data.activeAgentRuns, 0);
    await tester.pump();
    expect(find.text('Project diagnostics'), findsWidgets);
    debugPrint('LIVE: diagnostics loaded: 2 work items, queued run not active');

    await live.advance(project.id, const [
      ProjectStatus.implementation,
      ProjectStatus.testing,
      ProjectStatus.reviewing,
      ProjectStatus.humanReview,
    ]);
    await live.otherApprove(project.id);
    await waitUntil(
      tester,
      'Completed in the diagnostics',
      () =>
          diagnostics.state.value.dataOrNull?.projectStatus ==
          ProjectStatus.completed,
      timeout: const Duration(seconds: 60),
    );
    await waitUntil(
      tester,
      'polling to stop',
      () => !diagnostics.live.isInFlight && !diagnostics.live.hasScheduledTick,
    );
    debugPrint('LIVE: diagnostics saw Completed and stopped polling');

    await tester.pumpWidget(const SizedBox.shrink());
    await Get.deleteAll(force: true);
  }, skip: !LiveBackend.enabled);
}
