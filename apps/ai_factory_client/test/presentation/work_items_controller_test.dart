import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:ai_factory_client/presentation/models/work_items_data.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

void main() {
  late FakeProjectRepository projects;
  late FakeWorkItemRepository workItems;

  setUp(() {
    projects = FakeProjectRepository();
    workItems = FakeWorkItemRepository();
  });

  /// `onInit` is what the route binding calls; a unit test has to call it.
  WorkItemsController buildController({String projectId = 'p1'}) =>
      WorkItemsController(
        projectId: projectId,
        projects: projects,
        workItems: workItems,
      )..onInit();

  group('load', () {
    test('falls back to the flat list when no plan exists (404)', () async {
      workItems.workItems = [testWorkItem(id: 'w1'), testWorkItem(id: 'w2')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      final state = controller.state.value;
      expect(state, isA<Success<WorkItemsData>>());
      final data = (state as Success<WorkItemsData>).data;
      expect(data.hasPlan, isFalse);
      expect(data.workItems, hasLength(2));
      expect(projects.calls, contains('getPlan:p1'));
      expect(workItems.calls, contains('listByProject:p1'));
    });

    test(
      'uses the plan when one exists, joined with live work items',
      () async {
        final w1 = testWorkItem(id: 'w1', title: 'Analysis');
        final w2 = testWorkItem(id: 'w2', title: 'Implementation');
        projects.planResult = ProjectPlan(
          projectId: 'p1',
          planHash: 'h1',
          nodes: [
            testWorkItemNode(workItem: w1, planKey: 'T01'),
            testWorkItemNode(
              workItem: w2,
              planKey: 'T02',
              dependsOnPlanKeys: ['T01'],
              dependsOnWorkItemIds: ['w1'],
            ),
          ],
        );
        final controller = buildController();
        await Future<void>.delayed(Duration.zero);

        final data = (controller.state.value as Success<WorkItemsData>).data;
        expect(data.hasPlan, isTrue);
        expect(data.workItems.map((w) => w.id), ['w1', 'w2']);
        // A plan exists, so `listByProject` is never called — the join uses
        // the plan's own work items.
        expect(workItems.calls, isNot(contains('listByProject:p1')));
      },
    );

    test('reports Empty when the project has no work items at all', () async {
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Empty<WorkItemsData>>());
    });

    test('a getPlan failure surfaces as Failure', () async {
      projects.planError = const NetworkError();
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Failure<WorkItemsData>>());
    });

    test('a listByProject failure (no plan) surfaces as Failure', () async {
      workItems.listError = const NetworkError();
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Failure<WorkItemsData>>());
    });
  });

  group('refresh', () {
    test('keeps data visible while refreshing, via Refreshing', () async {
      workItems.workItems = [testWorkItem(id: 'w1')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Success<WorkItemsData>>());

      final gate = Completer<void>();
      projects.gate = gate.future;
      final refresh = controller.refreshData();
      expect(controller.state.value, isA<Refreshing<WorkItemsData>>());

      gate.complete();
      await refresh;
      expect(controller.state.value, isA<Success<WorkItemsData>>());
    });

    test('a superseded load does not overwrite the newer state', () async {
      final firstGate = Completer<void>();
      projects.gate = firstGate.future;
      workItems.workItems = [testWorkItem(id: 'w1')];
      final controller = buildController();
      final first = controller.load();

      // Fire a second load before the first (still gated) call returns.
      projects.gate = null;
      final second = controller.load();
      await second;
      final afterSecond = controller.state.value;

      firstGate.complete();
      await first;
      await Future<void>.delayed(Duration.zero);

      expect(controller.state.value, afterSecond);
    });
  });

  group('selection and readiness', () {
    test('selecting a work item fetches its readiness lazily', () async {
      workItems.workItems = [testWorkItem(id: 'w1')];
      workItems.readinessById['w1'] = TaskReadiness.blockedByFailedDependency;
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      expect(controller.readinessState.value, isA<Idle<TaskReadiness>>());
      controller.selectWorkItem('w1');
      expect(controller.selectedWorkItemId.value, 'w1');
      await Future<void>.delayed(Duration.zero);

      expect(controller.readinessState.value, isA<Success<TaskReadiness>>());
      expect(
        (controller.readinessState.value as Success<TaskReadiness>).data,
        TaskReadiness.blockedByFailedDependency,
      );
      expect(workItems.getReadinessCount, 1);
    });

    test('clearing the selection resets readiness to Idle', () async {
      workItems.workItems = [testWorkItem(id: 'w1')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      controller.selectWorkItem('w1');
      await Future<void>.delayed(Duration.zero);
      expect(controller.readinessState.value, isA<Success<TaskReadiness>>());

      controller.selectWorkItem(null);
      expect(controller.readinessState.value, isA<Idle<TaskReadiness>>());
    });

    test('a readiness failure surfaces without blanking the list', () async {
      workItems.workItems = [testWorkItem(id: 'w1')];
      workItems.readinessError = const NetworkError();
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      controller.selectWorkItem('w1');
      await Future<void>.delayed(Duration.zero);

      expect(controller.readinessState.value, isA<Failure<TaskReadiness>>());
      expect(controller.state.value, isA<Success<WorkItemsData>>());
    });
  });

  group('disposal', () {
    test('onClose cancels in-flight requests without throwing', () async {
      final gate = Completer<void>();
      projects.gate = gate.future;
      final controller = buildController();
      controller.onClose();
      gate.complete();
      await Future<void>.delayed(Duration.zero);
      // No assertion beyond "did not throw": cancellation must be silent.
    });
  });
}
