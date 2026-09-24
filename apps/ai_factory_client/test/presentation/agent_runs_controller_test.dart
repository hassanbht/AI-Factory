import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/models/agent_runs_data.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

void main() {
  late FakeAgentRunRepository agentRuns;

  setUp(() {
    agentRuns = FakeAgentRunRepository(definitions: []);
  });

  /// `onInit` is what the route binding calls; a unit test has to call it.
  AgentRunsController buildController({String projectId = 'p1'}) =>
      AgentRunsController(projectId: projectId, agentRuns: agentRuns)..onInit();

  group('load', () {
    test('joins runs with their agent definitions', () async {
      agentRuns.runs = [testAgentRun(id: 'r1', agentDefinitionId: 'def1')];
      agentRuns.definitions = [
        testAgentDefinition(id: 'def1', name: 'Implementation Agent'),
      ];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      final state = controller.state.value;
      expect(state, isA<Success<AgentRunsData>>());
      final data = (state as Success<AgentRunsData>).data;
      expect(data.runs, hasLength(1));
      expect(data.definitionFor(data.runs.first)?.name, 'Implementation Agent');
    });

    test('reports Empty when the project has no agent runs', () async {
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Empty<AgentRunsData>>());
    });

    test('a list failure surfaces as Failure', () async {
      agentRuns.listError = const NetworkError();
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Failure<AgentRunsData>>());
    });

    test('a listDefinitions failure surfaces as Failure', () async {
      agentRuns.runs = [testAgentRun()];
      agentRuns.definitionsError = const NetworkError();
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Failure<AgentRunsData>>());
    });

    test('the query is scoped to the project', () async {
      buildController(projectId: 'proj-7');
      await Future<void>.delayed(Duration.zero);
      expect(agentRuns.listQueries.single.projectId, 'proj-7');
    });
  });

  group('refresh', () {
    test('keeps data visible while refreshing, via Refreshing', () async {
      agentRuns.runs = [testAgentRun(id: 'r1')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Success<AgentRunsData>>());

      final gate = Completer<void>();
      agentRuns.gate = gate.future;
      final refresh = controller.refreshData();
      expect(controller.state.value, isA<Refreshing<AgentRunsData>>());

      gate.complete();
      await refresh;
      expect(controller.state.value, isA<Success<AgentRunsData>>());
    });

    test('a superseded load does not overwrite the newer state', () async {
      final firstGate = Completer<void>();
      agentRuns.gate = firstGate.future;
      agentRuns.runs = [testAgentRun(id: 'r1')];
      final controller = buildController();
      final first = controller.load();

      agentRuns.gate = null;
      final second = controller.load();
      await second;
      final afterSecond = controller.state.value;

      firstGate.complete();
      await first;
      await Future<void>.delayed(Duration.zero);

      expect(controller.state.value, afterSecond);
    });
  });

  group('status filter', () {
    test('re-queries the server with the selected status', () async {
      agentRuns.runs = [
        testAgentRun(id: 'r1', status: AgentRunStatus.succeeded),
        testAgentRun(id: 'r2', status: AgentRunStatus.failed),
      ];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      await controller.setStatusFilter(AgentRunStatus.failed);

      expect(controller.statusFilter.value, AgentRunStatus.failed);
      expect(agentRuns.listQueries.last.status, AgentRunStatus.failed);
      final data = (controller.state.value as Success<AgentRunsData>).data;
      expect(data.runs.map((r) => r.id), ['r2']);
    });
  });

  group('work item filter', () {
    test('is client-side and issues no new request', () async {
      agentRuns.runs = [testAgentRun(id: 'r1', workItemId: 'w1')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      final countBefore = agentRuns.listCount;
      controller.filterByWorkItem('w1');

      expect(controller.workItemFilter.value, 'w1');
      expect(agentRuns.listCount, countBefore);
    });

    test('clears the selection when set', () async {
      agentRuns.runs = [testAgentRun(id: 'r1', workItemId: 'w1')];
      final controller = buildController();
      await Future<void>.delayed(Duration.zero);

      controller.selectAgentRun('r1');
      controller.filterByWorkItem('w1');

      expect(controller.selectedAgentRunId.value, isNull);
    });
  });

  group('selection', () {
    test('selectAgentRun sets and clears the selected id', () async {
      final controller = buildController();
      controller.selectAgentRun('r1');
      expect(controller.selectedAgentRunId.value, 'r1');
      controller.selectAgentRun(null);
      expect(controller.selectedAgentRunId.value, isNull);
    });
  });

  group('disposal', () {
    test('onClose cancels in-flight requests without throwing', () async {
      final gate = Completer<void>();
      agentRuns.gate = gate.future;
      final controller = buildController();
      controller.onClose();
      gate.complete();
      await Future<void>.delayed(Duration.zero);
    });
  });
}
