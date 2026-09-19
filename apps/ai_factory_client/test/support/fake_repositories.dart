import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/request_cancellation.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/operations.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';

/// Repository doubles for controller and widget tests.
///
/// They implement the same interfaces the production repositories do, so a
/// controller under test is wired exactly as it is at runtime — only the
/// bottom of the chain is replaced. Repository-to-HTTP behaviour is covered
/// separately by the Phase 19.2 tests against a mocked transport.

/// Builds a [Project] with sensible defaults, so a test states only the field
/// it actually cares about.
Project testProject({
  String id = 'p1',
  String name = 'Customer API',
  String? slug,
  ProjectStatus status = ProjectStatus.analyzing,
  String? rawStatus,
  String? description = 'A customer-facing API.',
  DateTime? createdAtUtc,
  DateTime? updatedAtUtc,
  DateTime? lifecycleStartedAtUtc,
  String? currentError,
}) {
  final created = createdAtUtc ?? DateTime.utc(2026, 9, 19, 10);
  return Project(
    id: id,
    name: name,
    slug: slug ?? name.toLowerCase().replaceAll(' ', '-'),
    description: description,
    status: status,
    rawStatus: rawStatus ?? status.wireValue,
    createdAtUtc: created,
    updatedAtUtc: updatedAtUtc ?? created,
    lifecycleStartedAtUtc: lifecycleStartedAtUtc,
    currentError: currentError,
  );
}

SystemHealth testHealth({bool alive = true, bool ready = true}) => SystemHealth(
  isAlive: alive,
  isReady: ready,
  checkedAtUtc: DateTime.utc(2026, 9, 19, 12),
);

SchedulerStatus testScheduler({bool enabled = true, DateTime? lastCycle}) =>
    SchedulerStatus(
      enabled: enabled,
      maxConcurrentTasks: 4,
      lastCycleAtUtc: lastCycle,
      lastCycleDispatchedCount: 1,
      lastCycleFailureCount: 0,
      lastCycleSkippedDueToConcurrency: 0,
      totalDispatched: 12,
      totalFailures: 0,
    );

const AgentMetrics testAgentMetrics = AgentMetrics(
  queued: 2,
  running: 1,
  created: 5,
  started: 4,
  succeeded: 3,
  failed: 0,
  timedOut: 0,
  cancelled: 0,
  workerClaimSuccess: 4,
);

/// A scripted [ProjectRepository].
///
/// Every method either returns what the test staged or throws what the test
/// staged, and every call is recorded — which is how the duplicate-submission
/// tests prove that one press produced exactly one request.
class FakeProjectRepository implements ProjectRepository {
  FakeProjectRepository({List<Project>? projects})
    : projects = projects ?? <Project>[];

  List<Project> projects;

  /// Thrown by [list] when set.
  AppError? listError;

  /// Thrown by [create], [initializeSource] and [start] when set. Cleared
  /// after one throw when [failOnce] is true, so a test can assert that a
  /// retry succeeds.
  AppError? mutationError;
  bool failOnce = false;

  /// Completed by the test to control when an in-flight call finishes, which
  /// is what makes "the button is disabled while pending" observable.
  Future<void>? gate;

  Project? createResult;
  Project? sourceResult;
  Project? startResult;

  final List<String> calls = <String>[];
  final List<CreateProjectRequest> createRequests = <CreateProjectRequest>[];
  final List<String> startedIds = <String>[];

  int get listCount => calls.where((c) => c == 'list').length;

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  void _maybeThrow() {
    final error = mutationError;
    if (error == null) return;
    if (failOnce) mutationError = null;
    throw error;
  }

  @override
  Future<List<Project>> list({RequestCancellation? cancellation}) async {
    calls.add('list');
    await _await();
    if (listError != null) throw listError!;
    return List<Project>.unmodifiable(projects);
  }

  @override
  Future<Project> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getById:$id');
    await _await();
    if (listError != null) throw listError!;
    return projects.firstWhere(
      (p) => p.id == id,
      orElse: () => throw const NotFoundError(),
    );
  }

  @override
  Future<Project> create(
    CreateProjectRequest request, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('create');
    createRequests.add(request);
    await _await();
    _maybeThrow();
    final created =
        createResult ??
        testProject(
          id: 'created-${createRequests.length}',
          name: request.name,
          status: ProjectStatus.created,
        );
    projects = <Project>[...projects, created];
    return created;
  }

  @override
  Future<Project> initializeSource(
    String id,
    InitializeProjectSourceRequest request, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('initializeSource:$id');
    await _await();
    _maybeThrow();
    return sourceResult ??
        testProject(id: id, status: ProjectStatus.analyzing);
  }

  @override
  Future<Project> start(
    String id, {
    String? actor,
    RequestCancellation? cancellation,
  }) async {
    calls.add('start:$id');
    startedIds.add(id);
    await _await();
    _maybeThrow();
    final started =
        startResult ??
        testProject(
          id: id,
          status: ProjectStatus.analyzing,
          lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
        );
    projects = <Project>[
      for (final project in projects)
        if (project.id == id) started else project,
    ];
    return started;
  }

  @override
  Future<Project> updateStatus(
    String id,
    ProjectStatus status, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('updateStatus:$id');
    await _await();
    _maybeThrow();
    return testProject(id: id, status: status);
  }

  @override
  Future<ProjectPlan?> getPlan(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getPlan:$projectId');
    return null;
  }
}

class FakeOperationsRepository implements OperationsRepository {
  SystemHealth? health = testHealth();
  SchedulerStatus? scheduler = testScheduler();
  AppError? healthError;
  AppError? schedulerError;

  final List<String> calls = <String>[];

  @override
  Future<SystemHealth> getHealth({RequestCancellation? cancellation}) async {
    calls.add('health');
    if (healthError != null) throw healthError!;
    return health!;
  }

  @override
  Future<SchedulerStatus> getSchedulerStatus({
    RequestCancellation? cancellation,
  }) async {
    calls.add('scheduler');
    if (schedulerError != null) throw schedulerError!;
    return scheduler!;
  }

  @override
  Future<DeploymentInfo> getDeployment({RequestCancellation? cancellation}) =>
      throw UnimplementedError('not used before Phase 19.8');

  @override
  Future<DatabaseInfo> getDatabase({RequestCancellation? cancellation}) =>
      throw UnimplementedError('not used before Phase 19.8');

  @override
  Future<ConfigurationInfo> getConfiguration({
    RequestCancellation? cancellation,
  }) => throw UnimplementedError('not used before Phase 19.8');
}

class FakeDiagnosticsRepository implements DiagnosticsRepository {
  AgentMetrics? metrics = testAgentMetrics;

  /// Defaults to the real-world case: no operational key is configured, so
  /// the endpoint answers 401.
  AppError? metricsError;

  final List<String> calls = <String>[];

  @override
  Future<AgentMetrics> getAgentMetrics({
    RequestCancellation? cancellation,
  }) async {
    calls.add('agentMetrics');
    if (metricsError != null) throw metricsError!;
    return metrics!;
  }

  @override
  Future<ProjectDiagnostics> getProjectDiagnostics(
    String projectId, {
    RequestCancellation? cancellation,
  }) => throw UnimplementedError('not used before Phase 19.8');
}
