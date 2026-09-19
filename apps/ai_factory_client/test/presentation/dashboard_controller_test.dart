import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:ai_factory_client/presentation/models/dashboard_snapshot.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

void main() {
  late FakeProjectRepository projects;
  late FakeOperationsRepository operations;
  late FakeDiagnosticsRepository diagnostics;

  setUp(() {
    projects = FakeProjectRepository();
    operations = FakeOperationsRepository();
    diagnostics = FakeDiagnosticsRepository();
  });

  DashboardController build() => DashboardController(
    projects: projects,
    operations: operations,
    diagnostics: diagnostics,
  );

  group('loading', () {
    test('starts Idle and loads on init', () async {
      final controller = build();
      expect(controller.state.value, isA<Idle<DashboardSnapshot>>());

      controller.onInit();
      expect(controller.state.value, isA<Loading<DashboardSnapshot>>());

      await controller.load();
      expect(controller.state.value, isA<Success<DashboardSnapshot>>());
    });

    test('reads exactly the four endpoints the screen shows, once', () async {
      final controller = build();
      await controller.load();

      // One project request, not one per band: the counts, the attention band
      // and the recent band are all derived from the same response.
      expect(projects.listCount, 1);
      expect(operations.calls, <String>['health', 'scheduler']);
      expect(diagnostics.calls, <String>['agentMetrics']);
    });
  });

  group('counts', () {
    test('aggregates every project status group', () async {
      projects.projects = [
        testProject(id: '1', status: ProjectStatus.created),
        testProject(id: '2', status: ProjectStatus.implementation),
        testProject(id: '3', status: ProjectStatus.reviewing),
        testProject(id: '4', status: ProjectStatus.humanReview),
        testProject(id: '5', status: ProjectStatus.paused),
        testProject(id: '6', status: ProjectStatus.completed),
        testProject(id: '7', status: ProjectStatus.failed),
        testProject(id: '8', status: ProjectStatus.cancelled),
      ];

      final controller = build();
      await controller.load();
      final counts = controller.state.value.dataOrNull!.counts;

      expect(counts.total, 8);
      expect(counts.active, 2);
      expect(counts.notStarted, 1);
      expect(counts.humanReview, 1);
      expect(counts.paused, 1);
      expect(counts.completed, 1);
      expect(counts.failed, 1);
      expect(counts.cancelled, 1);
    });

    test('an unrecognized status is counted in the total only', () async {
      // A state a newer backend introduced must not be folded into a group
      // this client happens to understand.
      projects.projects = [
        testProject(
          id: '1',
          status: ProjectStatus.unknown,
          rawStatus: 'Negotiating',
        ),
      ];

      final controller = build();
      await controller.load();
      final counts = controller.state.value.dataOrNull!.counts;

      expect(counts.total, 1);
      expect(counts.active, 0);
      expect(counts.completed, 0);
      expect(counts.failed, 0);
    });
  });

  group('needs attention', () {
    test('orders human review, then failed, then recorded errors', () async {
      projects.projects = [
        testProject(id: 'ok', status: ProjectStatus.implementation),
        testProject(
          id: 'err',
          status: ProjectStatus.repairing,
          currentError: 'build failed',
        ),
        testProject(id: 'failed', status: ProjectStatus.failed),
        testProject(id: 'review', status: ProjectStatus.humanReview),
      ];

      final controller = build();
      await controller.load();

      expect(
        controller.state.value.dataOrNull!.needsAttention
            .map((p) => p.id)
            .toList(),
        <String>['review', 'failed', 'err'],
      );
    });

    test('is empty when nothing requires a person', () async {
      projects.projects = [
        testProject(id: '1', status: ProjectStatus.implementation),
        testProject(id: '2', status: ProjectStatus.completed),
      ];

      final controller = build();
      await controller.load();

      expect(controller.state.value.dataOrNull!.needsAttention, isEmpty);
    });
  });

  group('partial results', () {
    test(
      'a failing health probe leaves project data on screen and is reported',
      () async {
        projects.projects = [testProject()];
        operations.healthError = const NetworkError();

        final controller = build();
        await controller.load();

        final state = controller.state.value;
        expect(state, isA<Partial<DashboardSnapshot>>());
        expect((state as Partial<DashboardSnapshot>).failures, hasLength(1));
        expect(state.data.counts.total, 1);
        // Null is what makes the tile render "Not available" rather than a
        // value nobody can vouch for.
        expect(state.data.health, isNull);
        expect(state.data.scheduler, isNotNull);
      },
    );

    test('a 401 on agent metrics is not a failure', () async {
      // The operational key is simply not configured yet, which is the normal
      // state until Phase 19.8 — the screen says so instead of showing an
      // error the operator cannot act on.
      diagnostics.metricsError = const AuthenticationError();

      final controller = build();
      await controller.load();

      expect(controller.state.value, isA<Success<DashboardSnapshot>>());
      final snapshot = controller.state.value.dataOrNull!;
      expect(snapshot.agentMetricsRequiresKey, isTrue);
      expect(snapshot.agentMetrics, isNull);
    });

    test('any other agent-metrics failure is reported as partial', () async {
      diagnostics.metricsError = const ServerError();

      final controller = build();
      await controller.load();

      expect(controller.state.value, isA<Partial<DashboardSnapshot>>());
      expect(
        controller.state.value.dataOrNull!.agentMetricsRequiresKey,
        isFalse,
      );
    });
  });

  group('failure and refresh', () {
    test('a failing project list fails the screen', () async {
      projects.listError = const NetworkError();

      final controller = build();
      await controller.load();

      expect(controller.state.value, isA<Failure<DashboardSnapshot>>());
      expect(
        (controller.state.value as Failure<DashboardSnapshot>).error,
        isA<NetworkError>(),
      );
    });

    test('retrying after a failure succeeds', () async {
      projects.listError = const NetworkError();
      final controller = build();
      await controller.load();
      expect(controller.state.value, isA<Failure<DashboardSnapshot>>());

      projects.listError = null;
      projects.projects = [testProject()];
      await controller.load();

      expect(controller.state.value, isA<Success<DashboardSnapshot>>());
    });

    test('refresh keeps the previous data visible while it runs', () async {
      projects.projects = [testProject()];
      final controller = build();
      await controller.load();

      final pending = controller.refreshData();
      final during = controller.state.value;

      expect(during, isA<Refreshing<DashboardSnapshot>>());
      expect(during.dataOrNull!.counts.total, 1);

      await pending;
      expect(controller.state.value, isA<Success<DashboardSnapshot>>());
    });

    test('a failed refresh keeps the last good data as stale', () async {
      projects.projects = [testProject()];
      final controller = build();
      await controller.load();

      projects.listError = const TimeoutError();
      await controller.refreshData();

      final state = controller.state.value;
      expect(state, isA<Failure<DashboardSnapshot>>());
      expect(state.dataOrNull, isNotNull);
    });
  });

  test('closing the controller cancels in-flight work', () async {
    final controller = build();
    unawaited(controller.load());
    controller.onClose();

    // The point is that disposal does not leave a pending write into a dead
    // controller; reaching here without an exception is the assertion.
    expect(controller.state.value.isBusy, isTrue);
  });
}
