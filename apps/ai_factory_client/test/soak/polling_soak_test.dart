import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/network/request_cancellation.dart';
import 'package:ai_factory_client/core/polling/app_activity.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/core/utils/app_clock.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/operations.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/dashboard_controller.dart';
import 'package:ai_factory_client/presentation/controllers/diagnostics_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operations_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operator_access_controller.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/controllers/projects_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

/// Hours of polling, compressed into fake time.
///
/// Every live screen is driven for six simulated hours against repositories
/// that answer with latency, fail on a fixed (seeded) schedule — scattered
/// 5xx and network errors and a ten-minute outage — and are metered by a
/// model of the backend's fixed-window rate limiter (`GeneralApi` 100/min,
/// `OperationalApi` 50/min, health probes unlimited). Recorded per screen:
/// polls, successes, failures, backoff events, the longest scheduled delay,
/// the largest number of concurrent requests (total and per endpoint), the
/// busiest sliding 60-second window per policy, modelled HTTP 429s, and the
/// pending timers between cycles. Results go to
/// `build/perf/polling_soak.json` (git-ignored).
///
/// This is deterministic evidence about the **client's** request pattern.
/// It is not a load test of the backend; the live run in
/// `integration_test/soak_rate_limit_test.dart` is.
void main() {
  const intervals = PollIntervals.defaults;
  final report = <String, Object?>{};

  setUp(() => Get.testMode = true);
  tearDown(() async {
    AppClock.reset();
    await Get.deleteAll(force: true);
  });
  tearDownAll(() {
    final file = File('build/perf/polling_soak.json')
      ..createSync(recursive: true)
      ..writeAsStringSync(const JsonEncoder.withIndent('  ').convert(report));
    // ignore: avoid_print
    print('Polling soak:\n${file.readAsStringSync()}');
  });

  group('six-hour soak per screen', () {
    for (final screen in _Screen.values) {
      test(screen.name, () {
        fakeAsync((async) {
          // "Now" follows fake time, so every attempt and every success gets
          // its own timestamp.
          AppClock.follow(() => DateTime.utc(2026).add(async.elapsed));
          final traffic = _Traffic(() => async.elapsed, seed: 42);
          final session = screen.open(traffic, AppActivity());
          final live = session.live;

          var attempts = 0;
          var successes = 0;
          var backoffs = 0;
          var longestDelay = Duration.zero;
          var maxTimersIdle = 0;
          live.lastAttempt.listen((_) => attempts++);
          live.lastSuccessfulUpdate.listen((_) => successes++);
          // Every failed cycle schedules the next one with backoff.
          live.failureCount.listen((n) {
            if (n > 0) backoffs++;
          });

          async.flushMicrotasks();
          live.start();

          const step = Duration(seconds: 1);
          for (var t = Duration.zero; t < const Duration(hours: 6); t += step) {
            async.elapse(step);
            if (live.isInFlight) continue;
            maxTimersIdle = max(maxTimersIdle, async.pendingTimers.length);
            final next = live.nextDelay;
            if (next != null && next > longestDelay) longestDelay = next;
          }

          session.close();
          final callsAtClose = traffic.requests.length;
          async.elapse(const Duration(hours: 1));

          report['soak_${screen.name}'] = {
            'simulated': '6h',
            'baseIntervalSeconds': screen.interval(intervals).inSeconds,
            'polls': attempts,
            'successfulPolls': successes,
            'failedPolls': attempts - successes,
            'injectedFailures': traffic.failuresServed,
            'backoffEvents': backoffs,
            'longestScheduledDelaySeconds': longestDelay.inSeconds,
            'requests': traffic.requests.length,
            'maxConcurrentRequests': traffic.maxInFlight,
            'maxConcurrentPerEndpoint': traffic.maxPerEndpoint,
            'busiestMinuteByPolicy': traffic.busiestMinute(),
            'modelledHttp429': traffic.rateLimited,
            'maxPendingTimersBetweenCycles': maxTimersIdle,
            'requestsAfterDisposal': traffic.requests.length - callsAtClose,
          };

          expect(traffic.maxPerEndpoint, 1, reason: 'request overlap');
          expect(maxTimersIdle, lessThanOrEqualTo(1), reason: 'timer leak');
          expect(longestDelay, lessThanOrEqualTo(intervals.maxBackoff));
          expect(backoffs, greaterThan(0), reason: 'the outage backs off');
          expect(successes, greaterThan(attempts ~/ 2));
          expect(live.failureCount.value, 0, reason: 'recovered at the end');
          expect(traffic.rateLimited, 0, reason: 'modelled HTTP 429');
          for (final entry in traffic.busiestMinute().entries) {
            final limit = _Traffic.limits[entry.key];
            if (limit == null) continue;
            expect(entry.value, lessThanOrEqualTo(limit), reason: entry.key);
          }
          expect(traffic.requests.length, callsAtClose);
          expect(async.pendingTimers, isEmpty);
        });
      });
    }
  });

  group('lifecycle soak', () {
    test('500 background/foreground switches: polls only in the foreground, '
        'an immediate refresh on every resume, never a second timer', () {
      fakeAsync((async) {
        final traffic = _Traffic(() => async.elapsed, seed: 7, failures: false);
        final activity = AppActivity();
        final session = _Screen.projectDetail.open(traffic, activity);
        final live = session.live;
        async.flushMicrotasks();
        live.start();
        async.elapse(const Duration(seconds: 10));

        final random = Random(7);
        for (var i = 0; i < 500; i++) {
          activity.isForeground.value = false;
          async.elapse(const Duration(seconds: 1)); // a cycle may finish
          final before = traffic.requests.length;
          async.elapse(Duration(seconds: 5 + random.nextInt(120)));
          expect(traffic.requests.length, before, reason: 'polled while away');
          expect(live.hasScheduledTick, isFalse);

          activity.isForeground.value = true;
          async.elapse(const Duration(seconds: 1));
          expect(
            traffic.requests.length,
            greaterThan(before),
            reason: 'no immediate refresh on resume $i',
          );
          async.elapse(Duration(seconds: random.nextInt(20)));
          if (!live.isInFlight) {
            expect(async.pendingTimers.length, lessThanOrEqualTo(1));
          }
        }
        report['lifecycle_soak'] = {
          'switches': 500,
          'requests': traffic.requests.length,
          'maxConcurrentPerEndpoint': traffic.maxPerEndpoint,
          'modelledHttp429': traffic.rateLimited,
        };
        expect(traffic.maxPerEndpoint, 1);
        expect(traffic.rateLimited, 0);
        session.close();
        expect(async.pendingTimers, isEmpty);
      });
    });

    test('a hidden screen stops; uncovered, it refreshes at once', () {
      fakeAsync((async) {
        final traffic = _Traffic(() => async.elapsed, seed: 1, failures: false);
        final session = _Screen.dashboard.open(traffic, AppActivity());
        final live = session.live;
        async.flushMicrotasks();
        live.start();
        async.elapse(const Duration(minutes: 1));

        live.pause(PollPause.hidden);
        async.elapse(const Duration(seconds: 1));
        final hidden = traffic.requests.length;
        async.elapse(const Duration(minutes: 30));
        expect(traffic.requests.length, hidden);
        expect(async.pendingTimers, isEmpty);

        live.resume(PollPause.hidden);
        async.elapse(const Duration(seconds: 1));
        expect(traffic.requests.length, greaterThan(hidden));
        session.close();
      });
    });

    test('a terminal project stops polling for good', () {
      fakeAsync((async) {
        final traffic = _Traffic(() => async.elapsed, seed: 3, failures: false);
        final session = _Screen.projectDetail.open(traffic, AppActivity());
        final live = session.live;
        async.flushMicrotasks();
        live.start();
        async.elapse(const Duration(minutes: 5));

        traffic.projectStatus = ProjectStatus.completed;
        async.elapse(const Duration(minutes: 1));
        final stopped = traffic.requests.length;
        async.elapse(const Duration(hours: 2));
        expect(traffic.requests.length, stopped);
        expect(live.hasScheduledTick, isFalse);
        expect(async.pendingTimers, isEmpty);
        session.close();
      });
    });
  });

  group('leak detection', () {
    for (final screen in _Screen.values) {
      test('${screen.name}: open → poll → leave, 200 times', () {
        fakeAsync((async) {
          final traffic = _Traffic(
            () => async.elapsed,
            seed: 11,
            failures: false,
          );
          final activity = AppActivity();
          final baseline = PollingCoordinator.debugLiveCount;

          for (var i = 0; i < 200; i++) {
            final session = screen.open(traffic, activity);
            async.flushMicrotasks();
            session.live.start();
            async.elapse(const Duration(seconds: 7));
            session.close();
            expect(session.live.isDisposed, isTrue);
            expect(PollingCoordinator.debugLiveCount, baseline);
          }
          async.elapse(const Duration(seconds: 1)); // last reads finish

          // Nothing survives disposal: no timer, no request, and the app
          // lifecycle no longer reaches any of the 200 coordinators.
          expect(async.pendingTimers, isEmpty);
          final after = traffic.requests.length;
          async.elapse(const Duration(hours: 1));
          activity.isForeground.value = false;
          activity.isForeground.value = true;
          async.elapse(const Duration(minutes: 5));
          expect(traffic.requests.length, after);
          report['leak_${screen.name}'] = {
            'cycles': 200,
            'liveCoordinatorsAfter':
                PollingCoordinator.debugLiveCount - baseline,
            'pendingTimersAfter': async.pendingTimers.length,
            'requestsAfterDisposal': traffic.requests.length - after,
          };
        });
      });
    }
  });
}

/// A screen under soak: its controller(s), wired to [_Traffic].
enum _Screen {
  dashboard,
  projects,
  projectDetail,
  diagnostics,
  operations;

  Duration interval(PollIntervals i) => switch (this) {
    dashboard => i.dashboard,
    projects => i.projects,
    // An agent run stays queued the whole time: the fastest interval.
    projectDetail => i.agentRuns,
    diagnostics => i.diagnostics,
    operations => i.operations,
  };

  _Session open(_Traffic traffic, AppActivity activity) {
    switch (this) {
      case dashboard:
        final c = DashboardController(
          projects: traffic.projects,
          operations: traffic.operations,
          diagnostics: traffic.diagnostics,
          activity: activity,
        )..onInit();
        return _Session(c.live, c.onClose);
      case projects:
        final c = ProjectsController(
          projects: traffic.projects,
          activity: activity,
        )..onInit();
        return _Session(c.live, c.onClose);
      case projectDetail:
        final workItems = WorkItemsController(
          projectId: 'p1',
          projects: traffic.projects,
          workItems: traffic.workItems,
        )..onInit();
        final runs = AgentRunsController(
          projectId: 'p1',
          agentRuns: traffic.agentRuns,
        )..onInit();
        final c = ProjectDetailController(
          projectId: 'p1',
          projects: traffic.projects,
          workItems: workItems,
          agentRuns: runs,
          activity: activity,
        )..onInit();
        return _Session(c.live, () {
          c.onClose();
          workItems.onClose();
          runs.onClose();
        });
      case diagnostics:
        final c = DiagnosticsController(
          projectId: 'p1',
          diagnostics: traffic.diagnostics,
          activity: activity,
        )..onInit();
        return _Session(c.live, c.onClose);
      case operations:
        final c = OperationsController(
          operations: traffic.operations,
          diagnostics: traffic.diagnostics,
          access: OperatorAccessController(
            store: InMemoryOperationalCredentialStore(),
            client: ApiClient(
              config: const AppConfig(
                environment: AppEnvironment.development,
                apiBaseUrl: 'http://localhost:5223',
                connectTimeout: Duration(seconds: 1),
                sendTimeout: Duration(seconds: 1),
                receiveTimeout: Duration(seconds: 1),
              ),
            ),
          ),
          activity: activity,
        )..onInit();
        return _Session(c.live, c.onClose);
    }
  }
}

class _Session {
  const _Session(this.live, this.close);

  final PollingCoordinator live;
  final void Function() close;
}

/// Latency, concurrency, scripted failures and the backend's rate limits.
class _Traffic {
  _Traffic(this._now, {required int seed, this._failures = true})
    : _random = Random(seed) {
    projects = _Projects(this);
    workItems = _WorkItems(this);
    agentRuns = _AgentRuns(this);
    diagnostics = _Diagnostics(this);
    operations = _Operations(this);
  }

  final Duration Function() _now;
  final Random _random;
  final bool _failures;

  late final _Projects projects;
  late final _WorkItems workItems;
  late final _AgentRuns agentRuns;
  late final _Diagnostics diagnostics;
  late final _Operations operations;

  ProjectStatus projectStatus = ProjectStatus.implementation;

  static const Duration latency = Duration(milliseconds: 350);

  /// Backend policy per endpoint (`Program.cs` `RequireRateLimiting`).
  static const Map<String, String> policy = <String, String>{
    'projects': 'GeneralApi',
    'project': 'GeneralApi',
    'plan': 'GeneralApi',
    'tasks': 'GeneralApi',
    'runs': 'GeneralApi',
    'definitions': 'GeneralApi',
    'scheduler': 'GeneralApi',
    'metrics': 'OperationalApi',
    'diagnostics': 'OperationalApi',
    'deployment': 'OperationalApi',
    'database': 'OperationalApi',
    'configuration': 'OperationalApi',
    'health': 'none',
  };
  static const Map<String, int> limits = <String, int>{
    'GeneralApi': 100,
    'OperationalApi': 50,
  };

  final List<(Duration, String)> requests = <(Duration, String)>[];
  final Map<String, int> _inFlightByEndpoint = <String, int>{};
  final Map<String, Map<int, int>> _fixedWindows = <String, Map<int, int>>{};
  int _inFlight = 0;
  int maxInFlight = 0;
  int maxPerEndpoint = 0;
  int failuresServed = 0;
  int rateLimited = 0;

  AppError? _scriptedFailure(Duration at) {
    if (!_failures) return null;
    // A ten-minute outage an hour in, then scattered failures.
    if (at >= const Duration(hours: 1) &&
        at < const Duration(hours: 1, minutes: 10)) {
      return const NetworkError();
    }
    // The last half hour is clean, so the run must end recovered.
    if (at > const Duration(hours: 5, minutes: 30)) return null;
    final roll = _random.nextDouble();
    if (roll < 0.02) return const ServerError();
    if (roll < 0.04) return const NetworkError();
    return null;
  }

  Future<T> call<T>(String endpoint, T Function() answer) async {
    final at = _now();
    requests.add((at, endpoint));
    _inFlight++;
    final current = (_inFlightByEndpoint[endpoint] ?? 0) + 1;
    _inFlightByEndpoint[endpoint] = current;
    maxInFlight = max(maxInFlight, _inFlight);
    maxPerEndpoint = max(maxPerEndpoint, current);
    try {
      await Future<void>.delayed(latency);
      final policyName = policy[endpoint] ?? 'GeneralApi';
      final limit = limits[policyName];
      if (limit != null) {
        final window = _fixedWindows.putIfAbsent(policyName, () => {});
        final index = at.inSeconds ~/ 60;
        final used = (window[index] ?? 0) + 1;
        window[index] = used;
        if (used > limit) {
          rateLimited++;
          throw const RateLimitError();
        }
      }
      final failure = _scriptedFailure(at);
      if (failure != null) {
        failuresServed++;
        throw failure;
      }
      return answer();
    } finally {
      _inFlight--;
      _inFlightByEndpoint[endpoint] = current - 1;
    }
  }

  /// Requests in the busiest 60-second window, per policy — sliding, so
  /// the figure holds whatever the backend's window alignment.
  Map<String, int> busiestMinute() {
    final byPolicy = <String, List<Duration>>{};
    for (final (at, endpoint) in requests) {
      (byPolicy[policy[endpoint] ?? 'GeneralApi'] ??= []).add(at);
    }
    int busiest(List<Duration> times) {
      var best = 0;
      var start = 0;
      for (var end = 0; end < times.length; end++) {
        while (times[end] - times[start] >= const Duration(seconds: 60)) {
          start++;
        }
        best = max(best, end - start + 1);
      }
      return best;
    }

    return {for (final e in byPolicy.entries) e.key: busiest(e.value)};
  }
}

class _Projects extends FakeProjectRepository {
  _Projects(this._t);

  final _Traffic _t;

  Project get _project => testProject(id: 'p1', status: _t.projectStatus);

  @override
  Future<List<Project>> list({RequestCancellation? cancellation}) =>
      _t.call('projects', () => [_project, testProject(id: 'p2')]);

  @override
  Future<Project> getById(String id, {RequestCancellation? cancellation}) =>
      _t.call('project', () => _project);

  @override
  Future<ProjectPlan?> getPlan(
    String projectId, {
    RequestCancellation? cancellation,
  }) => _t.call(
    'plan',
    () => ProjectPlan(
      projectId: projectId,
      planHash: 'h',
      nodes: [testWorkItemNode(planKey: 'T01')],
    ),
  );
}

class _WorkItems extends FakeWorkItemRepository {
  _WorkItems(this._t);

  final _Traffic _t;

  @override
  Future<List<WorkItem>> listByProject(
    String projectId, {
    RequestCancellation? cancellation,
  }) => _t.call('tasks', () => [testWorkItem()]);
}

class _AgentRuns extends FakeAgentRunRepository {
  _AgentRuns(this._t);

  final _Traffic _t;

  @override
  Future<PagedResult<AgentRun>> list(
    AgentRunQuery query, {
    RequestCancellation? cancellation,
  }) => _t.call(
    'runs',
    () => PagedResult<AgentRun>(
      items: [testAgentRun(status: AgentRunStatus.queued)],
      totalCount: 1,
      page: 1,
      pageSize: query.pageSize,
    ),
  );

  @override
  Future<List<AgentDefinition>> listDefinitions({
    RequestCancellation? cancellation,
  }) => _t.call('definitions', () => [testAgentDefinition()]);
}

class _Diagnostics extends FakeDiagnosticsRepository {
  _Diagnostics(this._t);

  final _Traffic _t;

  @override
  Future<AgentMetrics> getAgentMetrics({RequestCancellation? cancellation}) =>
      _t.call('metrics', () => testAgentMetrics);

  @override
  Future<ProjectDiagnostics> getProjectDiagnostics(
    String projectId, {
    RequestCancellation? cancellation,
  }) => _t.call(
    'diagnostics',
    () => testProjectDiagnostics(projectStatus: _t.projectStatus),
  );
}

class _Operations extends FakeOperationsRepository {
  _Operations(this._t);

  final _Traffic _t;

  @override
  Future<SystemHealth> getHealth({RequestCancellation? cancellation}) =>
      _t.call('health', testHealth);

  @override
  Future<SchedulerStatus> getSchedulerStatus({
    RequestCancellation? cancellation,
  }) => _t.call('scheduler', testScheduler);

  @override
  Future<DeploymentInfo> getDeployment({RequestCancellation? cancellation}) =>
      _t.call('deployment', testDeployment);

  @override
  Future<DatabaseInfo> getDatabase({RequestCancellation? cancellation}) =>
      _t.call('database', testDatabase);

  @override
  Future<ConfigurationInfo> getConfiguration({
    RequestCancellation? cancellation,
  }) => _t.call('configuration', testConfiguration);
}
