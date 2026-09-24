import 'dart:async';

import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/operations.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:ai_factory_client/presentation/controllers/diagnostics_controller.dart';
import 'package:ai_factory_client/presentation/controllers/human_review_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operations_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operator_access_controller.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/controllers/projects_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:ai_factory_client/presentation/models/project_list_filter.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

/// Every controller's live updates, under fake time.
///
/// These prove *polling* against scripted repositories. They are not
/// evidence of polling against a real backend.
void main() {
  const intervals = PollIntervals.defaults;

  setUp(() => Get.testMode = true);
  tearDown(() async => Get.deleteAll(force: true));

  group('Dashboard', () {
    test('polls, applies the new data, and stops on disposal', () {
      fakeAsync((async) {
        final projects = FakeProjectRepository(
          projects: [testProject(id: '1', status: ProjectStatus.planning)],
        );
        final controller = DashboardController(
          projects: projects,
          operations: FakeOperationsRepository(),
          diagnostics: FakeDiagnosticsRepository(),
        )..onInit();
        async.flushMicrotasks();
        expect(projects.listCount, 1);
        expect(controller.live.lastSuccessfulUpdate.value, isNotNull);

        controller.live.start();
        projects.projects = [
          ...projects.projects,
          testProject(id: '2', status: ProjectStatus.humanReview),
        ];
        async.elapse(intervals.dashboard);

        expect(projects.listCount, 2);
        expect(controller.state.value.dataOrNull?.counts.total, 2);

        controller.onClose();
        async.elapse(const Duration(minutes: 5));
        expect(projects.listCount, 2, reason: 'no poll after disposal');
        expect(async.pendingTimers, isEmpty);
      });
    });

    test('a background poll never replaces content with a loading state', () {
      fakeAsync((async) {
        final projects = FakeProjectRepository(projects: [testProject()]);
        final controller = DashboardController(
          projects: projects,
          operations: FakeOperationsRepository(),
          diagnostics: FakeDiagnosticsRepository(),
        )..onInit();
        async.flushMicrotasks();
        controller.live.start();

        final gate = Completer<void>();
        projects.gate = gate.future;
        async.elapse(intervals.dashboard);
        expect(controller.live.isInFlight, isTrue);
        expect(
          controller.state.value,
          isA<Success<Object?>>(),
          reason: 'silent: no Refreshing, no Loading, no progress line',
        );

        gate.complete();
        async.flushMicrotasks();
        controller.onClose();
      });
    });

    test('with no active project it polls at the idle interval', () {
      fakeAsync((async) {
        final controller = DashboardController(
          projects: FakeProjectRepository(
            projects: [testProject(status: ProjectStatus.completed)],
          ),
          operations: FakeOperationsRepository(),
          diagnostics: FakeDiagnosticsRepository(),
        )..onInit();
        async.flushMicrotasks();
        expect(controller.live.nextDelay, intervals.idle);
        controller.onClose();
      });
    });
  });

  group('Projects', () {
    test('a poll keeps the filter, the query and the sort order', () {
      fakeAsync((async) {
        final repository = FakeProjectRepository(
          projects: [
            testProject(id: 'a', name: 'Alpha API'),
            testProject(id: 'b', name: 'Beta API'),
            testProject(
              id: 'c',
              name: 'Gamma API',
              status: ProjectStatus.completed,
            ),
          ],
        );
        final controller = ProjectsController(projects: repository)..onInit();
        async.flushMicrotasks();
        controller
          ..setStatusFilter(ProjectStatusFilter.active)
          ..setQuery('api');
        final filterBefore = controller.filter.value;
        controller.live.start();

        // The server now reports a fourth active project.
        repository.projects = [
          ...repository.projects,
          testProject(id: 'd', name: 'Delta API'),
        ];
        async.elapse(intervals.projects);

        expect(controller.filter.value, filterBefore);
        expect(
          controller.visibleProjects.map((p) => p.id),
          ProjectSort.apply(repository.projects.where(filterBefore.matches))
              .map((p) => p.id),
          reason: 'refresh applied; filter and ordering rule unchanged',
        );
        expect(controller.visibleProjects.map((p) => p.id), contains('d'));
        controller.onClose();
      });
    });

    test('a successful Start re-reads the list at once', () {
      fakeAsync((async) {
        final repository = FakeProjectRepository(projects: [testProject()]);
        final controller = ProjectsController(projects: repository)..onInit();
        async.flushMicrotasks();
        final before = repository.listCount;

        unawaited(controller.startProject('p1'));
        async.flushMicrotasks();

        expect(repository.listCount, before + 1);
        controller.onClose();
      });
    });
  });

  group('Project Detail', () {
    late FakeProjectRepository projects;
    late FakeWorkItemRepository workItems;
    late FakeAgentRunRepository agentRuns;

    setUp(() {
      projects = FakeProjectRepository(
        projects: [testProject(status: ProjectStatus.implementation)],
      );
      workItems = FakeWorkItemRepository(workItems: [testWorkItem()]);
      agentRuns = FakeAgentRunRepository(runs: [testAgentRun()]);
    });

    ({
      ProjectDetailController detail,
      WorkItemsController workItems,
      AgentRunsController agentRuns,
    })
    build() {
      final workItemsController = WorkItemsController(
        projectId: 'p1',
        projects: projects,
        workItems: workItems,
      )..onInit();
      final agentRunsController = AgentRunsController(
        projectId: 'p1',
        agentRuns: agentRuns,
      )..onInit();
      final detail = ProjectDetailController(
        projectId: 'p1',
        projects: projects,
        workItems: workItemsController,
        agentRuns: agentRunsController,
      )..onInit();
      return (
        detail: detail,
        workItems: workItemsController,
        agentRuns: agentRunsController,
      );
    }

    test('opening the screen reads each section exactly once', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();

        expect(projects.getByIdCount, 1);
        expect(workItems.listByProjectCount, 1);
        expect(agentRuns.listCount, 1);
        screen.detail.onClose();
      });
    });

    test('one cycle refreshes project, work items and agent runs', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();
        screen.detail.live.start();

        projects.projects = [
          testProject(name: 'Renamed', status: ProjectStatus.testing),
        ];
        workItems.workItems = [
          testWorkItem(),
          testWorkItem(id: 'w2', status: WorkItemStatus.running),
        ];
        agentRuns.runs = [
          testAgentRun(),
          testAgentRun(id: 'r2', status: AgentRunStatus.running),
        ];
        async.elapse(intervals.projectDetail);

        expect(projects.getByIdCount, 2);
        expect(workItems.listByProjectCount, 2);
        expect(agentRuns.listCount, 2);
        expect(screen.detail.project?.name, 'Renamed');
        expect(screen.detail.project?.status, ProjectStatus.testing);
        expect(
          screen.workItems.state.value.dataOrNull?.workItems,
          hasLength(2),
        );
        expect(screen.agentRuns.state.value.dataOrNull?.runs, hasLength(2));
        screen.detail.onClose();
      });
    });

    test('one timer for the whole screen, however often it is started', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();
        screen.detail.live
          ..start()
          ..start();

        expect(async.pendingTimers, hasLength(1));
        async.elapse(intervals.projectDetail * 3);
        expect(projects.getByIdCount, 1 + 3);
        expect(workItems.listByProjectCount, 1 + 3);
        expect(agentRuns.listCount, 1 + 3);
        screen.detail.onClose();
        expect(async.pendingTimers, isEmpty);
      });
    });

    test('a background poll does not re-read static agent definitions', () {
      fakeAsync((async) {
        int definitionReads() =>
            agentRuns.calls.where((c) => c == 'listDefinitions').length;
        final screen = build();
        async.flushMicrotasks();
        expect(definitionReads(), 1);

        screen.detail.live.start();
        async.elapse(intervals.projectDetail * 3);
        expect(agentRuns.listCount, 1 + 3, reason: 'runs are polled');
        expect(definitionReads(), 1, reason: 'definitions are not');

        unawaited(screen.detail.refreshData());
        async.flushMicrotasks();
        expect(definitionReads(), 2, reason: 'a manual refresh re-reads them');
        screen.detail.onClose();
      });
    });

    test('an executing agent run selects the fastest interval', () {
      fakeAsync((async) {
        agentRuns.runs = [testAgentRun(status: AgentRunStatus.running)];
        final screen = build();
        async.flushMicrotasks();
        expect(screen.detail.live.nextDelay, intervals.agentRuns);

        agentRuns.runs = [testAgentRun()];
        unawaited(screen.detail.refreshData());
        async.flushMicrotasks();
        expect(screen.detail.live.nextDelay, intervals.projectDetail);
        screen.detail.onClose();
      });
    });

    test('Created and Paused poll at the idle interval', () {
      fakeAsync((async) {
        for (final status in [ProjectStatus.created, ProjectStatus.paused]) {
          projects.projects = [testProject(status: status)];
          agentRuns.runs = [];
          workItems.workItems = [];
          final screen = build();
          async.flushMicrotasks();
          expect(
            screen.detail.live.nextDelay,
            intervals.idle,
            reason: '$status',
          );
          screen.detail.onClose();
        }
      });
    });

    test('a terminal project is not polled', () {
      fakeAsync((async) {
        for (final status in [
          ProjectStatus.completed,
          ProjectStatus.failed,
          ProjectStatus.cancelled,
        ]) {
          projects.projects = [testProject(status: status)];
          final screen = build();
          async.flushMicrotasks();
          final reads = projects.getByIdCount;
          screen.detail.live.start();

          expect(
            screen.detail.live.hasScheduledTick,
            isFalse,
            reason: '$status',
          );
          expect(screen.detail.live.status.value, LiveStatus.stopped);
          async.elapse(const Duration(minutes: 10));
          expect(projects.getByIdCount, reads);
          screen.detail.onClose();
        }
      });
    });

    test('a background poll keeps the project on screen', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();
        screen.detail.live.start();

        final gate = Completer<void>();
        projects.gate = gate.future;
        async.elapse(intervals.projectDetail);
        expect(screen.detail.state.value, isA<Success<Project>>());
        gate.complete();
        async.flushMicrotasks();
        screen.detail.onClose();
      });
    });

    test('a transient failure keeps the data, backs off and recovers', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();
        screen.detail.live.start();

        projects.getByIdError = const NetworkError();
        async.elapse(intervals.projectDetail);
        expect(screen.detail.project, isNotNull, reason: 'never blanked');
        expect(screen.detail.live.status.value, LiveStatus.offline);
        expect(screen.detail.live.nextDelay, intervals.projectDetail * 2);

        projects.getByIdError = null;
        async.elapse(intervals.projectDetail * 2);
        expect(screen.detail.state.value, isA<Success<Project>>());
        expect(screen.detail.live.status.value, LiveStatus.live);
        expect(screen.detail.live.nextDelay, intervals.projectDetail);
        screen.detail.onClose();
      });
    });

    test('a deleted project halts polling instead of looping on 404', () {
      fakeAsync((async) {
        final screen = build();
        async.flushMicrotasks();
        screen.detail.live.start();

        projects.getByIdError = const NotFoundError();
        async.elapse(intervals.projectDetail);
        expect(screen.detail.isNotFound, isTrue);
        expect(screen.detail.live.status.value, LiveStatus.halted);

        final reads = projects.getByIdCount;
        async.elapse(const Duration(minutes: 10));
        expect(projects.getByIdCount, reads);
        screen.detail.onClose();
      });
    });

    test('Start refreshes the whole screen at once', () {
      fakeAsync((async) {
        projects.projects = [testProject()];
        final screen = build();
        async.flushMicrotasks();
        final projectReads = projects.getByIdCount;
        final workItemReads = workItems.listByProjectCount;

        unawaited(screen.detail.startProject());
        async.flushMicrotasks();

        expect(projects.startCount, 1);
        expect(projects.getByIdCount, projectReads + 1);
        expect(workItems.listByProjectCount, workItemReads + 1);
        screen.detail.onClose();
      });
    });
  });

  group('Human Review', () {
    test("another operator's approval is observed, then polling stops", () {
      fakeAsync((async) {
        final projects = FakeProjectRepository(
          projects: [testProject(status: ProjectStatus.humanReview)],
        );
        final detail = ProjectDetailController(
          projectId: 'p1',
          projects: projects,
        )..onInit();
        async.flushMicrotasks();
        detail.live.start();
        expect(detail.live.nextDelay, intervals.humanReview);

        // Approved elsewhere: the server now reports Completed.
        projects.projects = [testProject(status: ProjectStatus.completed)];
        async.elapse(intervals.humanReview);

        expect(detail.project?.status, ProjectStatus.completed);
        expect(detail.live.hasScheduledTick, isFalse);
        expect(detail.live.status.value, LiveStatus.stopped);

        final reads = projects.getByIdCount;
        async.elapse(const Duration(minutes: 10));
        expect(projects.getByIdCount, reads);
        detail.onClose();
      });
    });

    test('an approval here refreshes at once through the same cycle', () {
      fakeAsync((async) {
        final projects = FakeProjectRepository(
          projects: [testProject(status: ProjectStatus.humanReview)],
        );
        final detail = ProjectDetailController(
          projectId: 'p1',
          projects: projects,
        )..onInit();
        async.flushMicrotasks();
        detail.live.start();

        final approval = FakeApprovalRepository();
        final review = HumanReviewController(
          projectId: 'p1',
          approval: approval,
          detail: detail,
        );
        projects.getByIdResult = testProject(status: ProjectStatus.completed);
        final reads = projects.getByIdCount;

        unawaited(review.approveProject());
        async.flushMicrotasks();

        expect(approval.approveCount, 1);
        expect(projects.getByIdCount, reads + 1);
        expect(detail.project?.status, ProjectStatus.completed);
        expect(detail.live.hasScheduledTick, isFalse);
        review.onClose();
        detail.onClose();
      });
    });
  });

  group('Operations', () {
    late FakeOperationsRepository operations;
    late FakeDiagnosticsRepository diagnostics;

    ApiClient buildClient() => ApiClient(
      config: const AppConfig(
        environment: AppEnvironment.development,
        apiBaseUrl: 'http://localhost:5223',
        connectTimeout: Duration(seconds: 10),
        sendTimeout: Duration(seconds: 30),
        receiveTimeout: Duration(seconds: 30),
      ),
    );

    OperationsController build() => OperationsController(
      operations: operations,
      diagnostics: diagnostics,
      access: OperatorAccessController(
        store: InMemoryOperationalCredentialStore(),
        client: buildClient(),
      ),
    )..onInit();

    int metricsCalls() =>
        diagnostics.calls.where((c) => c == 'agentMetrics').length;

    setUp(() {
      operations = FakeOperationsRepository();
      diagnostics = FakeDiagnosticsRepository();
    });

    test('a poll reads health, scheduler and metrics — nothing static', () {
      fakeAsync((async) {
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();
        async.elapse(intervals.operations);

        expect(operations.healthCount, 2);
        expect(operations.schedulerCount, 2);
        expect(metricsCalls(), 2);
        expect(operations.deploymentCount, 1);
        expect(operations.databaseCount, 1);
        expect(operations.configurationCount, 1);
        controller.onClose();
      });
    });

    test('a failing section leaves the others refreshing', () {
      fakeAsync((async) {
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();

        diagnostics.metricsError = const ServerError();
        operations.health = testHealth(ready: false);
        async.elapse(intervals.operations);

        final metrics = controller.metrics.value;
        expect(metrics, isA<Failure<Object?>>());
        expect(
          metrics.dataOrNull,
          isNotNull,
          reason: 'the last metrics stay on screen',
        );
        expect(controller.health.value.dataOrNull?.isReady, isFalse);
        expect(controller.deployment.value, isA<Success<DeploymentInfo>>());
        controller.onClose();
      });
    });

    test('a section that failed permanently is not polled again', () {
      fakeAsync((async) {
        operations.healthError = const NotFoundError();
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();
        async.elapse(intervals.operations * 2);

        expect(operations.healthCount, 1);
        expect(operations.schedulerCount, 3);
        controller.onClose();
      });
    });

    test('a manual refresh reads all six sections', () {
      fakeAsync((async) {
        final controller = build();
        async.flushMicrotasks();
        unawaited(controller.refreshData());
        async.flushMicrotasks();
        expect(operations.deploymentCount, 2);
        expect(operations.configurationCount, 2);
        controller.onClose();
      });
    });
  });

  group('Diagnostics', () {
    late FakeDiagnosticsRepository diagnostics;

    int reads() => diagnostics.calls
        .where((c) => c.startsWith('projectDiagnostics'))
        .length;

    DiagnosticsController build() =>
        DiagnosticsController(projectId: 'p1', diagnostics: diagnostics)
          ..onInit();

    setUp(() => diagnostics = FakeDiagnosticsRepository());

    test('an active project refreshes', () {
      fakeAsync((async) {
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();
        async.elapse(intervals.diagnostics * 2);
        expect(reads(), 3);
        controller.onClose();
      });
    });

    test('a waiting project refreshes at the idle interval', () {
      fakeAsync((async) {
        diagnostics.projectDiagnostics = testProjectDiagnostics(
          projectStatus: ProjectStatus.humanReview,
          activeAgentRuns: 0,
        );
        final controller = build();
        async.flushMicrotasks();
        expect(controller.live.nextDelay, intervals.idle);
        controller.onClose();
      });
    });

    test('a terminal project stops polling', () {
      fakeAsync((async) {
        diagnostics.projectDiagnostics = testProjectDiagnostics(
          projectStatus: ProjectStatus.completed,
          activeAgentRuns: 0,
        );
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();
        async.elapse(const Duration(minutes: 10));
        expect(reads(), 1);
        expect(controller.live.status.value, LiveStatus.stopped);
        controller.onClose();
      });
    });

    test('a 401 stops polling; entering a key resumes it', () {
      fakeAsync((async) {
        diagnostics.projectDiagnosticsError = const AuthenticationError();
        final controller = build();
        async.flushMicrotasks();
        controller.live.start();

        expect(controller.needsOperatorAccess, isTrue);
        expect(controller.live.status.value, LiveStatus.accessRequired);
        async.elapse(const Duration(minutes: 10));
        expect(reads(), 1, reason: 'the rejected key is never re-sent');

        // What the access panel's onSaved does.
        diagnostics.projectDiagnosticsError = null;
        unawaited(controller.load());
        async.flushMicrotasks();
        expect(controller.state.value, isA<Success<ProjectDiagnostics>>());
        expect(controller.live.status.value, LiveStatus.live);
        async.elapse(intervals.diagnostics);
        expect(reads(), 3);
        controller.onClose();
      });
    });
  });
}
