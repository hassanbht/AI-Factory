import 'dart:async';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/polling/app_activity.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/widgets/live_updates.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// The live-update indicator and the visibility scope, rendered.
///
/// Time is the widget tester's fake clock: `pump(duration)` fires the poll
/// timer. Nothing here contacts a backend.
void main() {
  setUpAll(initializeTestLocalization);

  late _Task task;

  setUp(() async {
    await registerTestServices();
    task = _Task();
  });

  PollingCoordinator poller({Duration? Function()? interval}) =>
      PollingCoordinator(
        task: task.call,
        interval: interval ?? () => const Duration(seconds: 5),
      );

  Future<void> showIndicator(
    WidgetTester tester,
    PollingCoordinator live, {
    Size size = TestSizes.expanded,
    Locale? locale,
    VoidCallback? onEnterKey,
  }) async {
    await setSurfaceSize(tester, size);
    await tester.pumpWidget(
      TestLocalizationScope(
        startLocale: locale,
        child: Scaffold(
          body: LiveStatusIndicator(poller: live, onEnterKey: onEnterKey),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('indicator', () {
    testWidgets('before the first update', (tester) async {
      await showIndicator(tester, poller());
      expect(find.text('Waiting for the first update'), findsOneWidget);
      expect(find.text('Not updated yet'), findsOneWidget);
    });

    testWidgets('live, with the time of the last successful update', (
      tester,
    ) async {
      final live = poller();
      await showIndicator(tester, live);
      await live.refreshNow();
      await tester.pump();

      expect(find.text('Live updates on'), findsOneWidget);
      expect(find.textContaining('Last updated '), findsOneWidget);
      expect(find.textContaining('Connected'), findsNothing);
      live.dispose();
    });

    testWidgets('the first load says refreshing; a later poll only spins', (
      tester,
    ) async {
      final live = poller();
      await showIndicator(tester, live);

      task.gate = Completer<void>();
      unawaited(live.refreshNow());
      await tester.pump();
      expect(find.text('Refreshing…'), findsOneWidget);
      task.gate!.complete();
      task.gate = null;
      await tester.pump();
      await tester.pump();

      task.gate = Completer<void>();
      unawaited(live.refreshNow());
      await tester.pump();
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(
        find.text('Live updates on'),
        findsOneWidget,
        reason: 'the words do not flicker on every poll',
      );
      task.gate!.complete();
      await tester.pump();
      await tester.pump();
      live.dispose();
    });

    testWidgets('offline keeps the last-updated time and offers a retry', (
      tester,
    ) async {
      final live = poller();
      await showIndicator(tester, live);
      await live.refreshNow();
      await tester.pump();
      final updated = live.lastSuccessfulUpdate.value;

      task.next = const NetworkError();
      await live.refreshNow();
      await tester.pump();

      expect(find.text('Offline · retrying automatically'), findsOneWidget);
      expect(live.lastSuccessfulUpdate.value, updated);
      expect(find.textContaining('Last updated '), findsOneWidget);

      task.next = null;
      await tester.tap(find.text('Retry now'));
      await tester.pump();
      await tester.pump();
      expect(find.text('Live updates on'), findsOneWidget);
      live.dispose();
    });

    testWidgets('a server error reads as a failed refresh', (tester) async {
      final live = poller();
      await showIndicator(tester, live);
      task.next = const ServerError();
      await live.refreshNow();
      await tester.pump();
      expect(
        find.text('Refresh failed · retrying automatically'),
        findsOneWidget,
      );
      live.dispose();
    });

    testWidgets('a permanent error stops live updates', (tester) async {
      final live = poller()..start();
      await showIndicator(tester, live);
      task.next = const NotFoundError();
      await live.refreshNow();
      await tester.pump();
      expect(
        find.text('Refresh failed · live updates stopped'),
        findsOneWidget,
      );
      expect(live.hasScheduledTick, isFalse);
      live.dispose();
    });

    testWidgets('access required offers the key entry', (tester) async {
      var asked = 0;
      final live = poller();
      await showIndicator(tester, live, onEnterKey: () => asked++);
      task.next = const AuthenticationError();
      await live.refreshNow();
      await tester.pump();

      expect(
        find.text('Access required · live updates stopped'),
        findsOneWidget,
      );
      await tester.tap(find.text('Enter operational key'));
      expect(asked, 1);
      live.dispose();
    });

    testWidgets('paused', (tester) async {
      final live = poller()
        ..start()
        ..pause(PollPause.hidden);
      await showIndicator(tester, live);
      expect(find.text('Live updates paused'), findsOneWidget);
      live.dispose();
    });

    testWidgets('stopped on a screen where nothing changes', (tester) async {
      final live = poller(interval: () => null)..start();
      await showIndicator(tester, live);
      await live.refreshNow();
      await tester.pump();
      expect(
        find.text('Not refreshing automatically — nothing here is changing'),
        findsOneWidget,
      );
      live.dispose();
    });

    testWidgets('German, at every width, without overflow', (tester) async {
      for (final size in [
        TestSizes.compact,
        TestSizes.medium,
        TestSizes.expanded,
      ]) {
        final live = poller();
        task.next = const ServerError();
        await showIndicator(
          tester,
          live,
          size: size,
          locale: const Locale('de'),
        );
        await live.refreshNow();
        await tester.pump();

        expect(
          find.text(
            'Aktualisierung fehlgeschlagen · neuer Versuch folgt automatisch',
          ),
          findsOneWidget,
          reason: '$size',
        );
        expect(find.text('Jetzt erneut versuchen'), findsOneWidget);
        expect(tester.takeException(), isNull, reason: 'overflow at $size');
        expectNoUntranslatedKeys(tester);
        live.dispose();
      }
    });

    testWidgets('English, at every width, without overflow', (tester) async {
      for (final size in [
        TestSizes.compact,
        TestSizes.medium,
        TestSizes.expanded,
      ]) {
        final live = poller();
        task.next = const AuthenticationError();
        await showIndicator(
          tester,
          live,
          size: size,
          locale: const Locale('en'),
          onEnterKey: () {},
        );
        await live.refreshNow();
        await tester.pump();
        expect(tester.takeException(), isNull, reason: 'overflow at $size');
        expectNoUntranslatedKeys(tester);
        live.dispose();
      }
    });
  });

  group('accessibility', () {
    testWidgets('the live region carries the settled status, so a routine '
        'poll is not announced', (tester) async {
      final handle = tester.ensureSemantics();
      final live = poller();
      await showIndicator(tester, live);
      await live.refreshNow();
      await tester.pump();

      final settled = tester.getSemantics(
        find.bySemanticsLabel('Live updates on'),
      );
      expect(settled.getSemanticsData().flagsCollection.isLiveRegion, isTrue);

      // A poll in flight: the live region's label does not change.
      task.gate = Completer<void>();
      unawaited(live.refreshNow());
      await tester.pump();
      expect(find.bySemanticsLabel('Live updates on'), findsOneWidget);
      expect(find.bySemanticsLabel('Refreshing…'), findsOneWidget);
      expect(
        tester
            .getSemantics(find.bySemanticsLabel('Refreshing…'))
            .getSemanticsData()
            .flagsCollection
            .isLiveRegion,
        isFalse,
        reason: 'reachable, but never read out on its own',
      );

      // A real transition — going offline — changes the live region.
      task.next = const NetworkError();
      task.gate!.complete();
      await tester.pump();
      await tester.pump();
      final offline = tester.getSemantics(
        find.bySemanticsLabel('Offline · retrying automatically'),
      );
      expect(offline.getSemanticsData().flagsCollection.isLiveRegion, isTrue);
      live.dispose();
      handle.dispose();
    });
  });

  group('visibility scope', () {
    testWidgets('mount starts, a covering page pauses, returning refreshes, '
        'unmount stops', (tester) async {
      final live = poller();
      await setSurfaceSize(tester, TestSizes.expanded);
      await tester.pumpWidget(
        TestAppScope(
          initialRoute: '/a',
          getPages: [
            GetPage<void>(
              name: '/a',
              page: () => LiveUpdatesScope(
                poller: live,
                child: Scaffold(
                  body: TextButton(
                    onPressed: () => Get.toNamed<void>('/b'),
                    child: const Text('open b'),
                  ),
                ),
              ),
            ),
            GetPage<void>(
              name: '/b',
              page: () => const Scaffold(body: Text('page b')),
            ),
          ],
        ),
      );
      await tester.pumpAndSettle();
      expect(live.isRunning, isTrue);

      await tester.pump(const Duration(seconds: 5));
      expect(task.calls, 1, reason: 'visible: ticking');

      await tester.tap(find.text('open b'));
      await tester.pumpAndSettle();
      expect(live.isPaused, isTrue);
      await tester.pump(const Duration(seconds: 30));
      expect(task.calls, 1, reason: 'hidden: not polled');

      Get.back<void>();
      await tester.pumpAndSettle();
      expect(live.isPaused, isFalse);
      expect(task.calls, 2, reason: 'refreshed immediately on return');
      expect(task.triggers.last, PollTrigger.resume);

      await tester.pumpWidget(const SizedBox.shrink());
      expect(live.isRunning, isFalse);
      expect(live.hasScheduledTick, isFalse);
      live.dispose();
    });

    testWidgets('a dialog does not pause the page beneath it', (tester) async {
      final live = poller();
      await setSurfaceSize(tester, TestSizes.expanded);
      await tester.pumpWidget(
        TestAppScope(
          home: LiveUpdatesScope(
            poller: live,
            child: Builder(
              builder: (context) => Scaffold(
                body: TextButton(
                  onPressed: () => showDialog<void>(
                    context: context,
                    builder: (_) => const AlertDialog(content: Text('confirm')),
                  ),
                  child: const Text('open dialog'),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('open dialog'));
      await tester.pumpAndSettle();
      expect(live.isPaused, isFalse);

      await tester.pumpWidget(const SizedBox.shrink());
      live.dispose();
    });
  });

  group('application lifecycle', () {
    testWidgets('hidden pauses; resumed refreshes immediately', (tester) async {
      final activity = AppActivity.fromBinding();
      final live = PollingCoordinator(
        task: task.call,
        interval: () => const Duration(seconds: 5),
        activity: activity,
      )..start();

      tester.binding
        ..handleAppLifecycleStateChanged(AppLifecycleState.inactive)
        ..handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      await tester.pump();
      expect(live.isPaused, isTrue);
      await tester.pump(const Duration(seconds: 30));
      expect(task.calls, 0);

      tester.binding
        ..handleAppLifecycleStateChanged(AppLifecycleState.inactive)
        ..handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
      expect(live.isPaused, isFalse);
      expect(task.calls, 1);

      live.dispose();
      activity.dispose();
    });
  });

  group('Project Detail page', () {
    const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
    late FakeProjectRepository projects;

    setUp(() {
      projects = FakeProjectRepository(
        projects: [
          testProject(
            id: projectId,
            name: 'Before',
            status: ProjectStatus.implementation,
          ),
        ],
      );
      Get.put<ProjectRepository>(projects);
      Get.put<WorkItemRepository>(FakeWorkItemRepository());
      Get.put<AgentRunRepository>(FakeAgentRunRepository());
      Get.put<ApprovalRepository>(FakeApprovalRepository());
      Get.put<OperationsRepository>(FakeOperationsRepository());
      Get.put<DiagnosticsRepository>(FakeDiagnosticsRepository());
      Get.put<AuditRepository>(FakeAuditRepository());
      Get.put<OperationalCredentialStore>(InMemoryOperationalCredentialStore());
      Get.put<ApiClient>(ApiClient(config: Get.find()));
    });

    testWidgets('shows a server-side change without a manual refresh, and '
        'stops once the project is finished', (tester) async {
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
      );
      expect(find.text('Before'), findsWidgets);
      expect(find.text('Live updates on'), findsOneWidget);

      projects.projects = [
        testProject(
          id: projectId,
          name: 'After',
          status: ProjectStatus.completed,
        ),
      ];
      await tester.pump(PollIntervals.defaults.projectDetail);
      await tester.pump();

      expect(find.text('After'), findsWidgets);
      expect(
        find.text('Not refreshing automatically — nothing here is changing'),
        findsOneWidget,
      );
      final live = Get.find<ProjectDetailController>().live;
      expect(live.hasScheduledTick, isFalse);
    });

    testWidgets('opening diagnostics pauses Project Detail polling', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        initialRoute: AppRoutes.projectDetail(projectId),
        getPages: AppRoutes.pages,
      );
      final live = Get.find<ProjectDetailController>().live;
      expect(live.isPaused, isFalse);

      await tester.tap(find.byIcon(Icons.monitor_heart_outlined));
      await tester.pumpAndSettle();
      expect(live.isPaused, isTrue);

      final reads = projects.getByIdCount;
      await tester.pump(const Duration(seconds: 30));
      expect(projects.getByIdCount, reads, reason: 'hidden: not polled');

      // Leave the tree so both screens' polling stops before teardown.
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });
}

class _Task {
  int calls = 0;
  final List<PollTrigger> triggers = <PollTrigger>[];
  AppError? next;
  Completer<void>? gate;

  Future<AppError?> call(PollTrigger trigger) async {
    calls++;
    triggers.add(trigger);
    final hold = gate;
    if (hold != null) await hold.future;
    return next;
  }
}
