import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/app/app.dart';
import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// "The application must remain useful even when the backend is unavailable."
///
/// Run it against a port nothing is listening on, so the failure is a real
/// transport failure rather than a stubbed one:
///
/// ```bash
/// flutter test integration_test/offline_flow_test.dart -d windows \
///   --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5999
/// ```
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('an unreachable API leaves a usable, honest application', (
    tester,
  ) async {
    // English pinned, not left to the host: on a German Windows machine the
    // "system" preference correctly resolves to German, and every English
    // assertion below would fail for a reason that is not a defect.
    SharedPreferences.setMockInitialValues(<String, Object>{
      LocaleService.storageKey: LocalePreference.english.storageValue,
    });
    await EasyLocalization.ensureInitialized();
    await Get.deleteAll(force: true);
    await AppBindings.initPreRunApp();

    // Guards the run itself: pointed at a live API these assertions would be
    // meaningless, so the test states which URL it needs.
    expect(
      Get.find<AppConfig>().apiBaseUrl,
      'http://localhost:5999',
      reason: 'run with --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5999',
    );

    await tester.pumpWidget(const AiFactoryAppRoot());
    await tester.pumpAndSettle(const Duration(seconds: 30));

    // --- Dashboard ---------------------------------------------------------
    expect(find.text('Something went wrong'), findsOneWidget);
    // A localized sentence naming the likely cause, not a transport dump.
    expect(find.textContaining('No connection'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
    expect(find.textContaining('SocketException'), findsNothing);
    expect(find.textContaining('DioException'), findsNothing);

    // The shell is still navigable: an unreachable API must not trap the
    // operator on a dead screen.
    await tester.tap(find.text('Projects').first);
    await tester.pumpAndSettle(const Duration(seconds: 30));

    expect(find.text('Something went wrong'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
    // Creation is still reachable; it will fail against the API and say so,
    // which is a better answer than a hidden button.
    expect(find.widgetWithText(FilledButton, 'New project'), findsOneWidget);

    // Retry re-issues the request rather than only re-rendering.
    await tester.tap(find.text('Retry'));
    await tester.pump();
    expect(find.byType(CircularProgressIndicator), findsWidgets);
    await tester.pumpAndSettle(const Duration(seconds: 30));
    expect(find.text('Retry'), findsOneWidget);

    // Settings is entirely local, so it still works — including the language
    // switch, which must not depend on the backend.
    await tester.tap(find.text('Settings').first);
    await tester.pumpAndSettle();

    expect(find.text('Appearance and language'), findsOneWidget);
    expect(find.text('Connection'), findsOneWidget);
    expect(find.text('http://localhost:5999'), findsOneWidget);
  });

  /// Phase 19.4, Workstream Z. A deep link into Project Detail is the one way
  /// to reach that screen with no API: there is no list to open it from.
  testWidgets('Project Detail is honest and navigable with no API', (
    tester,
  ) async {
    // English pinned, not left to the host: on a German Windows machine the
    // "system" preference correctly resolves to German, and every English
    // assertion below would fail for a reason that is not a defect.
    SharedPreferences.setMockInitialValues(<String, Object>{
      LocaleService.storageKey: LocalePreference.english.storageValue,
    });
    await EasyLocalization.ensureInitialized();
    await Get.deleteAll(force: true);
    await AppBindings.initPreRunApp();

    expect(
      Get.find<AppConfig>().apiBaseUrl,
      'http://localhost:5999',
      reason: 'run with --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5999',
    );

    await tester.pumpWidget(const AiFactoryAppRoot());
    await tester.pumpAndSettle(const Duration(seconds: 30));

    Get.toNamed<void>(
      AppRoutes.projectDetail('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
    );
    await tester.pumpAndSettle(const Duration(seconds: 30));

    expect(find.byType(ProjectDetailPage), findsOneWidget);

    // An unreachable API is a transport failure, not a missing project: the
    // screen must not claim the project does not exist.
    expect(find.text('Something went wrong'), findsOneWidget);
    expect(find.textContaining('No connection'), findsOneWidget);
    expect(find.text('Project not found'), findsNothing);
    expect(find.textContaining('SocketException'), findsNothing);
    expect(find.textContaining('DioException'), findsNothing);

    // Retry re-issues the request rather than only re-rendering.
    expect(find.text('Retry'), findsOneWidget);
    await tester.tap(find.text('Retry'));
    await tester.pump();
    expect(find.byType(CircularProgressIndicator), findsWidgets);
    await tester.pumpAndSettle(const Duration(seconds: 30));
    expect(find.text('Retry'), findsOneWidget);

    // Navigation still works: the operator is never trapped on a dead screen.
    await tester.tap(find.byType(BackButton));
    await tester.pumpAndSettle(const Duration(seconds: 30));
    expect(find.byType(ProjectDetailPage), findsNothing);

    await tester.tap(find.text('Settings').first);
    await tester.pumpAndSettle();
    expect(find.text('Appearance and language'), findsOneWidget);
  });
}
