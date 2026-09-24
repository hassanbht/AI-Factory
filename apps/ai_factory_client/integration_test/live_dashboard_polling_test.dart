import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/data/repositories/api_project_repository.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';

/// Phase 19.9 — the Dashboard refreshes on its own against a **real** API.
///
/// Opt-in and throwaway-database only, exactly like
/// `live_polling_flow_test.dart` (one scenario per file is a known limit of
/// this binding). A second, independent client creates and advances a
/// project; the Dashboard's counts follow without a manual refresh.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const enabled = bool.fromEnvironment('AI_FACTORY_LIVE_POLLING');
  const baseUrl = String.fromEnvironment(
    'AI_FACTORY_API_BASE_URL',
    defaultValue: 'http://127.0.0.1:5299',
  );

  setUpAll(initializeTestLocalization);

  testWidgets('Dashboard counts follow a project created elsewhere', (
    tester,
  ) async {
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
    await Get.delete<OperationalCredentialStore>(force: true);
    Get.put<OperationalCredentialStore>(InMemoryOperationalCredentialStore());

    final other = ApiProjectRepository(ApiClient(config: config));

    Get.testMode = true;
    await setSurfaceSize(tester, TestSizes.expanded);
    await tester.pumpWidget(
      TestAppScope(
        startLocale: const Locale('en'),
        initialRoute: AppRoutes.dashboard,
        getPages: AppRoutes.pages,
      ),
    );

    DashboardController dashboard() => Get.find<DashboardController>();

    Future<Duration> waitUntil(
      String what,
      bool Function() condition, {
      Duration timeout = const Duration(seconds: 60),
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
      'the first load',
      () => dashboard().state.value.dataOrNull != null,
    );
    final before = dashboard().state.value.dataOrNull!.counts;
    expect(find.text('Live updates on'), findsOneWidget);

    final created = await other.create(
      CreateProjectRequest(
        name: 'Live dashboard 19.9 ${DateTime.now().millisecondsSinceEpoch}',
      ),
    );
    await other.initializeSource(
      created.id,
      const InitializeProjectSourceRequest(sourceType: ProjectSourceType.empty),
    );
    final seen = await waitUntil(
      'the new project to be counted',
      () =>
          (dashboard().state.value.dataOrNull?.counts.total ?? 0) >
          before.total,
    );
    debugPrint('LIVE: Dashboard counted the new project after $seen');

    await other.updateStatus(created.id, ProjectStatus.specification);
    final seenActive = await waitUntil(
      'the active count to rise',
      () =>
          (dashboard().state.value.dataOrNull?.counts.active ?? 0) >
          before.active,
    );
    debugPrint('LIVE: Dashboard active count rose after $seenActive');

    await tester.pumpWidget(const SizedBox.shrink());
    await Get.deleteAll(force: true);
  }, skip: !enabled);
}
