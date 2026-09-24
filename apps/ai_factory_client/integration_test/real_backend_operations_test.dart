import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/presentation/controllers/operations_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';
import 'support/live_backend.dart';

/// Phase 19.10 real-backend matrix: the Operations screen, every section
/// read from a real API on a throwaway database, on native Windows.
///
///     flutter test integration_test/real_backend_operations_test.dart -d windows \
///       --dart-define=AI_FACTORY_LIVE=true \
///       --dart-define=AI_FACTORY_API_BASE_URL=http://127.0.0.1:5299
///
/// The throwaway API runs with the default `RequireAccessKey=false`, so no
/// operational key is needed or used. The `401` path is covered by the
/// widget and controller suites, not claimed here.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(initializeTestLocalization);

  testWidgets('Operations reads health, deployment, database, configuration, '
      'scheduler and metrics from the real API', (tester) async {
    await LiveBackend.start();

    Get.testMode = true;
    await setSurfaceSize(tester, const Size(1440, 3200));
    await tester.pumpWidget(
      TestAppScope(
        startLocale: const Locale('en'),
        initialRoute: AppRoutes.operations,
        getPages: AppRoutes.pages,
      ),
    );
    await waitUntil(
      tester,
      'the route to bind its controller',
      () => Get.isRegistered<OperationsController>(),
    );
    final operations = Get.find<OperationsController>();

    bool loaded(ViewState<Object?> state) => state is Success<Object?>;
    await waitUntil(tester, 'every section to load', () {
      return loaded(operations.health.value) &&
          loaded(operations.deployment.value) &&
          loaded(operations.database.value) &&
          loaded(operations.configuration.value) &&
          loaded(operations.scheduler.value) &&
          loaded(operations.metrics.value);
    });
    await tester.pump();

    expect(find.text('API health'), findsOneWidget);
    expect(find.text('Deployment'), findsOneWidget);
    expect(find.text('Database'), findsWidgets);
    expect(find.text('Development'), findsWidgets);
    expect(find.text('Sqlite'), findsWidgets);
    expect(find.text('Live updates on'), findsOneWidget);
    debugPrint('LIVE: all Operations sections loaded from the real API');

    await tester.pumpWidget(const SizedBox.shrink());
    await Get.deleteAll(force: true);
  }, skip: !LiveBackend.enabled);
}
