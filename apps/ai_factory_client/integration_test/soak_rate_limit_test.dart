import 'dart:convert';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/test_app.dart';
import 'support/live_backend.dart';

/// Phase 19.10 polling and rate-limit soak against a **real** API on a
/// throwaway database, on native Windows.
///
///     flutter test integration_test/soak_rate_limit_test.dart -d windows \
///       --dart-define=AI_FACTORY_LIVE=true \
///       --dart-define=AI_FACTORY_API_BASE_URL=http://127.0.0.1:5299 \
///       --dart-define=AI_FACTORY_SOAK_MINUTES=12
///
/// The real application walks every live screen for the whole duration,
/// over and over, with the navigation patterns that could duplicate polling:
/// open → leave → reopen, open → back → open, switching project, switching
/// language, and app pause → resume. One project has a queued agent run the
/// whole time, which keeps Project Detail on its fastest (3 s) interval.
///
/// Every request the application sends passes through a [RequestMeter].
/// The run fails on any `429`, and records: requests per endpoint, the
/// busiest 60-second window per backend rate-limit policy, overlapping
/// requests, failures, and the largest number of live polling
/// coordinators at any moment. The report is printed as `LIVE-SOAK {json}`.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(initializeTestLocalization);

  const minutes = int.fromEnvironment(
    'AI_FACTORY_SOAK_MINUTES',
    defaultValue: 12,
  );

  testWidgets(
    'every live screen, repeatedly, for $minutes minutes: 0 × 429',
    (tester) async {
      final live = await LiveBackend.start();
      registerAppActivity();
      final locale = Get.find<LocaleService>();
      final meter = RequestMeter();
      Get.find<ApiClient>().dio.interceptors.add(meter);

      final busy = await live.createProject('Soak busy 19.10');
      await live.advance(busy.id, const [
        ProjectStatus.specification,
        ProjectStatus.architecture,
        ProjectStatus.planning,
      ]);
      await live.submitPlan(busy.id);
      await live.queueAgentRun(busy.id);
      final quiet = await live.createProject('Soak quiet 19.10');

      Get.testMode = true;
      await setSurfaceSize(tester, const Size(1440, 1200));
      await tester.pumpWidget(
        TestAppScope(
          startLocale: const Locale('en'),
          initialRoute: AppRoutes.dashboard,
          getPages: AppRoutes.pages,
        ),
      );

      final baseline = PollingCoordinator.debugLiveCount;
      var maxLive = 0;
      var hiddenRequests = 0;
      var resumesRefreshed = 0;

      Future<void> dwell(Duration duration) async {
        final until = DateTime.now().add(duration);
        while (DateTime.now().isBefore(until)) {
          await tester.pump(const Duration(milliseconds: 100));
          final count = PollingCoordinator.debugLiveCount - baseline;
          if (count > maxLive) maxLive = count;
          await Future<void>.delayed(const Duration(milliseconds: 400));
        }
      }

      Future<void> go(String route) async {
        Get.offNamed<void>(route);
        await dwell(const Duration(milliseconds: 600));
      }

      Future<void> push(String route) async {
        Get.toNamed<void>(route);
        await dwell(const Duration(milliseconds: 600));
      }

      Future<void> back() async {
        Get.back<void>();
        await dwell(const Duration(milliseconds: 600));
      }

      // No pump while the app is hidden: the engine schedules no frame for a
      // hidden app, so a pump would wait forever. Real time passes instead.
      Future<void> lifecycle(List<AppLifecycleState> states) async {
        for (final state in states) {
          tester.binding.handleAppLifecycleStateChanged(state);
          await Future<void>.delayed(const Duration(milliseconds: 200));
        }
      }

      final deadline = DateTime.now().add(const Duration(minutes: minutes));
      var cycles = 0;
      while (DateTime.now().isBefore(deadline)) {
        cycles++;
        await dwell(const Duration(seconds: 15)); // Dashboard

        await go(AppRoutes.projects);
        await dwell(const Duration(seconds: 15));

        await push(AppRoutes.projectDetail(busy.id));
        await dwell(const Duration(seconds: 25));
        await push(AppRoutes.projectDiagnostics(busy.id));
        await dwell(const Duration(seconds: 10));
        await back();
        await back();

        // Switch project.
        await push(AppRoutes.projectDetail(quiet.id));
        await dwell(const Duration(seconds: 10));
        await back();

        // Open → back → open.
        await push(AppRoutes.projectDetail(busy.id));
        await back();
        await push(AppRoutes.projectDetail(busy.id));
        await dwell(const Duration(seconds: 10));
        await back();

        await go(AppRoutes.operations);
        await dwell(const Duration(seconds: 15));

        // Switch language on a live screen.
        await locale.apply(
          tester.element(find.byType(Scaffold).first),
          cycles.isOdd ? LocalePreference.german : LocalePreference.english,
        );
        await dwell(const Duration(seconds: 5));

        // App pause → resume: nothing may be requested while hidden.
        await lifecycle(const [
          AppLifecycleState.inactive,
          AppLifecycleState.hidden,
        ]);
        // An in-flight read may still land.
        await Future<void>.delayed(const Duration(seconds: 1));
        final before = meter.sent.length;
        await Future<void>.delayed(const Duration(seconds: 15));
        hiddenRequests += meter.sent.length - before;
        final beforeResume = meter.sent.length;
        await lifecycle(const [
          AppLifecycleState.inactive,
          AppLifecycleState.resumed,
        ]);
        await dwell(const Duration(seconds: 5));
        // Resuming refreshes the visible screen at once.
        if (meter.sent.length > beforeResume) resumesRefreshed++;

        await go(AppRoutes.dashboard);
      }

      // Leaving every live screen leaves nothing polling.
      await go(AppRoutes.settings);
      await dwell(const Duration(seconds: 3));
      final liveAfter = PollingCoordinator.debugLiveCount - baseline;
      final settled = meter.sent.length;
      await dwell(const Duration(seconds: 20));
      final afterLeaving = meter.sent.length - settled;

      bool operational(String e) =>
          e.contains('/api/operations') ||
          e.contains('/api/audit-log') ||
          e.contains('/api/agents/metrics');
      bool unlimited(String e) => e.contains('/health/');
      final perEndpoint = <String, int>{};
      for (final (_, endpoint) in meter.sent) {
        perEndpoint[endpoint] = (perEndpoint[endpoint] ?? 0) + 1;
      }
      final general = meter.busiestMinute(
        where: (e) => !operational(e) && !unlimited(e),
      );
      final operationalMinute = meter.busiestMinute(where: operational);
      final report = <String, Object?>{
        'minutes': minutes,
        'cycles': cycles,
        'requests': meter.sent.length,
        'requestsPerEndpoint': perEndpoint,
        'busiestMinuteGeneralApi': general,
        'busiestMinuteOperationalApi': operationalMinute,
        'http429': meter.rateLimited,
        'failedRequests': meter.failures,
        'maxConcurrentSameUrl': meter.maxPerEndpoint,
        'maxConcurrentTotal': meter.maxTotal,
        'maxLiveCoordinators': maxLive,
        'liveCoordinatorsAfterLeaving': liveAfter,
        'requestsWhileHidden': hiddenRequests,
        'resumesThatRefreshed': '$resumesRefreshed of $cycles',
        'requestsAfterLeavingLiveScreens': afterLeaving,
      };
      debugPrint('LIVE-SOAK ${jsonEncode(report)}');

      expect(meter.rateLimited, 0, reason: 'HTTP 429 during the soak');
      expect(maxLive, lessThanOrEqualTo(3), reason: 'duplicate pollers');
      expect(liveAfter, 0);
      expect(afterLeaving, 0);
      expect(hiddenRequests, 0);
      expect(resumesRefreshed, cycles, reason: 'a resume did not refresh');
      expect(general, lessThan(100));
      expect(operationalMinute, lessThan(50));

      await tester.pumpWidget(const SizedBox.shrink());
      await Get.deleteAll(force: true);
    },
    timeout: const Timeout(Duration(minutes: minutes + 10)),
    skip: !LiveBackend.enabled,
  );
}
