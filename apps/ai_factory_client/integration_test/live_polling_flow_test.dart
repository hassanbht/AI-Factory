import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/data/repositories/api_approval_repository.dart';
import 'package:ai_factory_client/data/repositories/api_project_repository.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';

/// Phase 19.9 — live polling against a **real** AI Factory API.
///
/// Opt-in: runs only with `--dart-define=AI_FACTORY_LIVE_POLLING=true`, and
/// only against an API started on a throwaway database. The run that
/// produced the Phase 19.9 report started the API with
/// `Database__ConnectionString` pointing at a temporary file, verified the
/// file path and all migrations from the API's own log before this test, and
/// never touched `C:\AI-Factory\data\factory.db`.
///
/// "Another operator" is a second, independent API client inside this test.
/// It drives the project through the backend's own transition table with
/// `PATCH /api/projects/{id}/status` and finally approves it with
/// `POST /api/projects/{id}/approve`. The screen under test is never told
/// about any of it and never refreshed by hand: every change it shows
/// arrives through its polling cycle.
///
/// Not covered live: work item and agent run changes. Those are produced by
/// the Worker and Hermes, which are not run here; they are covered by the
/// deterministic tests in `test/presentation/live_updates_controllers_test.dart`.
///
/// Known harness limits (see `operations_flow_test.dart`): one scenario per
/// file and no mid-test navigation, so the screen is opened directly.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const enabled = bool.fromEnvironment('AI_FACTORY_LIVE_POLLING');
  const baseUrl = String.fromEnvironment(
    'AI_FACTORY_API_BASE_URL',
    defaultValue: 'http://127.0.0.1:5299',
  );

  setUpAll(initializeTestLocalization);

  testWidgets(
    'Project Detail follows real server-side changes and stops when done',
    (tester) async {
      await registerTestServices();
      const config = AppConfig(
        environment: AppEnvironment.development,
        apiBaseUrl: baseUrl,
        connectTimeout: Duration(seconds: 10),
        sendTimeout: Duration(seconds: 30),
        receiveTimeout: Duration(seconds: 30),
      );
      await Get.delete<AppConfig>(force: true);
      Get.put<AppConfig>(config, permanent: true);
      AppBindings().dependencies();
      // Never a real keystore in a test.
      await Get.delete<OperationalCredentialStore>(force: true);
      Get.put<OperationalCredentialStore>(InMemoryOperationalCredentialStore());

      // The other operator: an independent client against the same API.
      final other = ApiClient(config: config);
      final otherProjects = ApiProjectRepository(other);
      final otherApproval = ApiApprovalRepository(other);

      final created = await otherProjects.create(
        CreateProjectRequest(
          name: 'Live polling 19.9 ${DateTime.now().millisecondsSinceEpoch}',
          description: 'Phase 19.9 live polling check on a throwaway database.',
        ),
      );
      await otherProjects.initializeSource(
        created.id,
        const InitializeProjectSourceRequest(
          sourceType: ProjectSourceType.empty,
        ),
      );

      Get.testMode = true;
      await setSurfaceSize(tester, TestSizes.expanded);
      await tester.pumpWidget(
        TestAppScope(
          startLocale: const Locale('en'),
          initialRoute: AppRoutes.projectDetail(created.id),
          getPages: AppRoutes.pages,
        ),
      );

      ProjectDetailController detail() => Get.find<ProjectDetailController>();

      Future<Duration> waitUntil(
        String what,
        bool Function() condition, {
        Duration timeout = const Duration(seconds: 40),
      }) async {
        final watch = Stopwatch()..start();
        while (watch.elapsed < timeout) {
          await tester.pump(const Duration(milliseconds: 100));
          if (condition()) return watch.elapsed;
          await Future<void>.delayed(const Duration(milliseconds: 150));
        }
        throw TestFailure('Timed out after $timeout waiting for: $what');
      }

      await waitUntil(
        'the project to load',
        () => detail().project?.status == ProjectStatus.analyzing,
      );
      expect(find.text('Live updates on'), findsOneWidget);

      Future<void> advance(List<ProjectStatus> path) async {
        for (final status in path) {
          await otherProjects.updateStatus(created.id, status);
        }
      }

      // External change 1: the Factory "advances" the project.
      await advance([
        ProjectStatus.specification,
        ProjectStatus.architecture,
        ProjectStatus.planning,
        ProjectStatus.implementation,
      ]);
      final seenImplementation = await waitUntil(
        'Implementation to be observed',
        () => detail().project?.status == ProjectStatus.implementation,
      );
      debugPrint('LIVE: Implementation observed after $seenImplementation');

      // External change 2: it reaches the human gate.
      await advance([
        ProjectStatus.testing,
        ProjectStatus.reviewing,
        ProjectStatus.humanReview,
      ]);
      final seenReview = await waitUntil(
        'HumanReview to be observed',
        () => detail().project?.status == ProjectStatus.humanReview,
      );
      await tester.pump();
      expect(find.text('Approve project'), findsOneWidget);
      debugPrint('LIVE: HumanReview observed after $seenReview');

      // External change 3: another operator approves it.
      await otherApproval.approve(created.id, actor: 'live-polling-test');
      final seenCompleted = await waitUntil(
        'Completed to be observed',
        () => detail().project?.status == ProjectStatus.completed,
      );
      debugPrint('LIVE: Completed observed after $seenCompleted');

      // The project arrives first; the cycle settles once its work item and
      // agent run reads in the same cycle have returned too.
      final live = detail().live;
      await waitUntil('the cycle to settle', () => !live.isInFlight);
      await tester.pump();
      expect(live.status.value, LiveStatus.stopped);
      expect(live.hasScheduledTick, isFalse);
      expect(
        find.text('Not refreshing automatically — nothing here is changing'),
        findsOneWidget,
      );

      // A terminal project is not polled: no attempt in well over one
      // interval.
      final lastAttempt = live.lastAttempt.value;
      await Future<void>.delayed(const Duration(seconds: 7));
      await tester.pump();
      expect(live.lastAttempt.value, lastAttempt);
      debugPrint('LIVE: no poll in 7 s after Completed (terminal stop)');

      await tester.pumpWidget(const SizedBox.shrink());
      await Get.deleteAll(force: true);
    },
    skip: !enabled,
  );
}
