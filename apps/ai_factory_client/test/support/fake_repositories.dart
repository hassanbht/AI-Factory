import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/request_cancellation.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/audit.dart';
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
  String? gitRepository,
  String? gitBranch,
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
    gitRepository: gitRepository,
    gitBranch: gitBranch,
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

  /// Thrown by [list] when set — and, for backwards compatibility with the
  /// Phase 19.3 tests, by [getById] as well unless [getByIdError] is set.
  AppError? listError;

  /// Thrown by [getById] when set, in preference to [listError]. Cleared after
  /// one throw when [failOnce] is true, so a test can assert that a retry or a
  /// refresh recovers.
  AppError? getByIdError;

  /// Returned by [getById] regardless of [projects] when set, which is how a
  /// test stages the *server's* answer to a refresh without also rewriting the
  /// list.
  Project? getByIdResult;

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

  int get getByIdCount => calls.where((c) => c.startsWith('getById:')).length;

  int get startCount => calls.where((c) => c.startsWith('start:')).length;

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
    final staged = getByIdError;
    if (staged != null) {
      if (failOnce) getByIdError = null;
      throw staged;
    }
    if (listError != null) throw listError!;
    final result = getByIdResult;
    if (result != null) return result;
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
    return sourceResult ?? testProject(id: id, status: ProjectStatus.analyzing);
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

  /// Staged answer for [getPlan]. Null (the default) means "no plan exists",
  /// which is the real 404 case, not a test omission.
  ProjectPlan? planResult;
  AppError? planError;

  @override
  Future<ProjectPlan?> getPlan(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getPlan:$projectId');
    await _await();
    if (planError != null) throw planError!;
    return planResult;
  }
}

/// Builds a [WorkItem] with sensible defaults, so a test states only the
/// field it actually cares about.
WorkItem testWorkItem({
  String id = 'w1',
  String projectId = 'p1',
  WorkItemType type = WorkItemType.implementation,
  String? rawType,
  String title = 'Implement the endpoint',
  String? description = 'Add the REST endpoint.',
  WorkItemStatus status = WorkItemStatus.ready,
  String? rawStatus,
  WorkItemPriority priority = WorkItemPriority.normal,
  String? rawPriority,
  int attemptCount = 0,
  int repairAttemptCount = 0,
  String? currentError,
  DateTime? lastHeartbeatAtUtc,
  DateTime? createdAtUtc,
  DateTime? updatedAtUtc,
}) {
  final created = createdAtUtc ?? DateTime.utc(2026, 9, 19, 10);
  return WorkItem(
    id: id,
    projectId: projectId,
    type: type,
    rawType: rawType ?? type.wireValue,
    title: title,
    description: description,
    status: status,
    rawStatus: rawStatus ?? status.wireValue,
    priority: priority,
    rawPriority: rawPriority ?? priority.wireValue,
    attemptCount: attemptCount,
    repairAttemptCount: repairAttemptCount,
    currentError: currentError,
    lastHeartbeatAtUtc: lastHeartbeatAtUtc,
    createdAtUtc: created,
    updatedAtUtc: updatedAtUtc ?? created,
  );
}

/// Builds a [WorkItemNode] wrapping [testWorkItem] defaults, for plan tests.
WorkItemNode testWorkItemNode({
  WorkItem? workItem,
  String planKey = 'T01',
  List<String> dependsOnPlanKeys = const <String>[],
  List<String> dependsOnWorkItemIds = const <String>[],
  TaskReadiness readiness = TaskReadiness.ready,
}) => WorkItemNode(
  workItem: workItem ?? testWorkItem(id: planKey.toLowerCase()),
  planKey: planKey,
  dependsOnPlanKeys: dependsOnPlanKeys,
  dependsOnWorkItemIds: dependsOnWorkItemIds,
  readiness: readiness,
);

/// A scripted [WorkItemRepository].
class FakeWorkItemRepository implements WorkItemRepository {
  FakeWorkItemRepository({List<WorkItem>? workItems})
    : workItems = workItems ?? <WorkItem>[];

  List<WorkItem> workItems;

  AppError? listError;
  AppError? getByIdError;

  /// Staged answer for [getReadiness], keyed by work item id. When a test
  /// does not stage an id, [defaultReadiness] is returned.
  final Map<String, TaskReadiness> readinessById = <String, TaskReadiness>{};
  TaskReadiness defaultReadiness = TaskReadiness.ready;
  AppError? readinessError;

  /// Completed by the test to control when an in-flight call finishes.
  Future<void>? gate;

  final List<String> calls = <String>[];

  int get listByProjectCount =>
      calls.where((c) => c.startsWith('listByProject:')).length;

  int get getReadinessCount =>
      calls.where((c) => c.startsWith('getReadiness:')).length;

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  @override
  Future<List<WorkItem>> listByProject(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('listByProject:$projectId');
    await _await();
    if (listError != null) throw listError!;
    return List<WorkItem>.unmodifiable(
      workItems.where((w) => w.projectId == projectId),
    );
  }

  @override
  Future<WorkItem> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getById:$id');
    await _await();
    if (getByIdError != null) throw getByIdError!;
    return workItems.firstWhere(
      (w) => w.id == id,
      orElse: () => throw const NotFoundError(),
    );
  }

  @override
  Future<TaskReadiness> getReadiness(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getReadiness:$id');
    await _await();
    if (readinessError != null) throw readinessError!;
    return readinessById[id] ?? defaultReadiness;
  }

  @override
  Future<WorkItem> updateStatus(
    String id,
    WorkItemStatus status, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('updateStatus:$id');
    await _await();
    final current = workItems.firstWhere(
      (w) => w.id == id,
      orElse: () => throw const NotFoundError(),
    );
    final updated = testWorkItem(
      id: current.id,
      projectId: current.projectId,
      title: current.title,
      status: status,
    );
    workItems = <WorkItem>[
      for (final item in workItems)
        if (item.id == id) updated else item,
    ];
    return updated;
  }
}

/// Builds an [AgentDefinition] with sensible defaults.
AgentDefinition testAgentDefinition({
  String id = 'def1',
  String name = 'Implementation Agent',
  WorkItemType agentType = WorkItemType.implementation,
  String? rawAgentType,
  bool enabled = true,
  DateTime? createdAtUtc,
  DateTime? updatedAtUtc,
}) {
  final created = createdAtUtc ?? DateTime.utc(2026, 9, 19, 10);
  return AgentDefinition(
    id: id,
    name: name,
    agentType: agentType,
    rawAgentType: rawAgentType ?? agentType.wireValue,
    enabled: enabled,
    createdAtUtc: created,
    updatedAtUtc: updatedAtUtc ?? created,
  );
}

/// Builds an [AgentRun] with sensible defaults, so a test states only the
/// field it actually cares about.
AgentRun testAgentRun({
  String id = 'r1',
  String? projectId = 'p1',
  String? workItemId = 'w1',
  String agentDefinitionId = 'def1',
  AgentRunStatus status = AgentRunStatus.succeeded,
  String? rawStatus,
  int attempt = 1,
  DateTime? createdAtUtc,
  DateTime? startedAtUtc,
  DateTime? completedAtUtc,
  Duration? duration,
  String? output,
  FailureCategory? failureCategory,
  String? rawFailureCategory,
  String? errorMessage,
  String correlationId = 'corr-1',
  StructuredAgentResult? structuredResult,
}) {
  final created = createdAtUtc ?? DateTime.utc(2026, 9, 19, 10);
  return AgentRun(
    id: id,
    projectId: projectId,
    workItemId: workItemId,
    agentDefinitionId: agentDefinitionId,
    status: status,
    rawStatus: rawStatus ?? status.wireValue,
    attempt: attempt,
    createdAtUtc: created,
    startedAtUtc: startedAtUtc,
    completedAtUtc: completedAtUtc,
    duration: duration,
    output: output,
    failureCategory: failureCategory,
    rawFailureCategory: rawFailureCategory ?? failureCategory?.wireValue,
    errorMessage: errorMessage,
    correlationId: correlationId,
    structuredResult: structuredResult,
  );
}

/// A scripted [AgentRunRepository].
class FakeAgentRunRepository implements AgentRunRepository {
  FakeAgentRunRepository({
    List<AgentRun>? runs,
    List<AgentDefinition>? definitions,
  }) : runs = runs ?? <AgentRun>[],
       definitions = definitions ?? <AgentDefinition>[testAgentDefinition()];

  List<AgentRun> runs;
  List<AgentDefinition> definitions;

  AppError? listError;
  AppError? definitionsError;
  AppError? getByIdError;
  AppError? cancelError;

  /// Completed by the test to control when an in-flight call finishes.
  Future<void>? gate;

  final List<String> calls = <String>[];
  final List<AgentRunQuery> listQueries = <AgentRunQuery>[];

  int get listCount => calls.where((c) => c == 'list').length;

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  @override
  Future<PagedResult<AgentRun>> list(
    AgentRunQuery query, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('list');
    listQueries.add(query);
    await _await();
    if (listError != null) throw listError!;

    final filtered = query.status == null
        ? runs
        : runs.where((r) => r.status == query.status).toList();
    return PagedResult<AgentRun>(
      items: List<AgentRun>.unmodifiable(filtered),
      totalCount: filtered.length,
      page: query.page,
      pageSize: query.pageSize,
    );
  }

  @override
  Future<AgentRun> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('getById:$id');
    await _await();
    if (getByIdError != null) throw getByIdError!;
    return runs.firstWhere(
      (r) => r.id == id,
      orElse: () => throw const NotFoundError(),
    );
  }

  @override
  Future<AgentRun> create(
    CreateAgentRunRequest request, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('create');
    await _await();
    final created = testAgentRun(
      id: 'created-${runs.length + 1}',
      agentDefinitionId: request.agentDefinitionId,
      projectId: request.projectId,
    );
    runs = <AgentRun>[...runs, created];
    return created;
  }

  @override
  Future<void> cancelRun(String id, {RequestCancellation? cancellation}) async {
    calls.add('cancelRun:$id');
    await _await();
    if (cancelError != null) throw cancelError!;
  }

  @override
  Future<List<AgentDefinition>> listDefinitions({
    RequestCancellation? cancellation,
  }) async {
    calls.add('listDefinitions');
    await _await();
    if (definitionsError != null) throw definitionsError!;
    return List<AgentDefinition>.unmodifiable(definitions);
  }
}

DeploymentInfo testDeployment({
  String environment = 'Development',
  String version = '1.0.0',
  bool isContainerized = false,
  Duration? uptime = const Duration(hours: 3, minutes: 12),
  String? gitCommit = 'abc1234',
  String? gitBranch = 'main',
}) => DeploymentInfo(
  applicationName: 'AI.Factory.Api',
  environment: environment,
  version: version,
  informationalVersion: '$version+abc1234',
  frameworkDescription: '.NET 10.0.0',
  osDescription: 'Microsoft Windows 10.0.22621',
  processArchitecture: 'X64',
  isContainerized: isContainerized,
  startedAtUtc: DateTime.utc(2026, 9, 19, 8),
  uptime: uptime,
  databaseProvider: 'Sqlite',
  gitCommit: gitCommit,
  gitBranch: gitBranch,
  buildNumber: '412',
  buildTimestamp: '2026-09-19T07:55:00Z',
);

DatabaseInfo testDatabase({
  bool isReady = true,
  int failuresCount = 0,
  String providerName = 'Sqlite',
}) => DatabaseInfo(
  generatedAtUtc: DateTime.utc(2026, 9, 19, 12),
  providerName: providerName,
  isReady: isReady,
  commandTimeoutSeconds: 30,
  retriesEnabled: true,
  maxRetryAttempts: 3,
  optimisticConcurrencyEnabled: true,
  operationsCount: 128,
  failuresCount: failuresCount,
  retriesCount: 2,
  transientFailuresCount: 1,
  concurrencyConflictsCount: 0,
  busyFailuresCount: 0,
  lockedFailuresCount: 0,
  transactionFailuresCount: 0,
  averageDurationMs: 4.25,
);

ConfigurationInfo testConfiguration({
  bool operationalEndpointsSecured = true,
  bool isValid = true,
  List<ProviderInfo>? providers,
}) => ConfigurationInfo(
  generatedAtUtc: DateTime.utc(2026, 9, 19, 12),
  environmentName: 'Development',
  isValid: isValid,
  databaseProvider: 'Sqlite',
  databaseConfigured: true,
  secretManagementEnabled: true,
  secretProviderName: 'Environment',
  operationalEndpointsSecured: operationalEndpointsSecured,
  operationalKeyHeader: 'X-Operations-Key',
  rateLimitingEnabled: true,
  maxRequestBodySizeBytes: 1048576,
  corsEnabled: false,
  httpsRedirectEnabled: true,
  hstsEnabled: true,
  resilienceProfileNames: const <String>['Default', 'Hermes'],
  providers:
      providers ??
      const <ProviderInfo>[
        ProviderInfo(
          name: 'OpenRouter',
          enabled: true,
          hasSecretReference: true,
          secretReferenceName: 'OpenRouter:ApiKey',
        ),
      ],
  configurationSources: const <String>['appsettings.json', 'Environment'],
);

/// A scripted [OperationsRepository].
///
/// Each endpoint has its own error slot and its own call count, because the
/// controller's contract is that the five reads succeed and fail
/// independently — a shared error would make that untestable.
class FakeOperationsRepository implements OperationsRepository {
  SystemHealth? health = testHealth();
  SchedulerStatus? scheduler = testScheduler();
  DeploymentInfo? deployment = testDeployment();
  DatabaseInfo? database = testDatabase();
  ConfigurationInfo? configuration = testConfiguration();

  AppError? healthError;
  AppError? schedulerError;
  AppError? deploymentError;
  AppError? databaseError;
  AppError? configurationError;

  /// Clears the matching error after it has been thrown once, which is how a
  /// test proves the controller's single post-`401` retry actually recovers.
  bool failDeploymentOnce = false;
  bool failDatabaseOnce = false;

  /// Completed by the test to control when in-flight calls finish.
  Future<void>? gate;

  final List<String> calls = <String>[];

  int get healthCount => calls.where((c) => c == 'health').length;
  int get schedulerCount => calls.where((c) => c == 'scheduler').length;
  int get deploymentCount => calls.where((c) => c == 'deployment').length;
  int get databaseCount => calls.where((c) => c == 'database').length;
  int get configurationCount => calls.where((c) => c == 'configuration').length;

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  @override
  Future<SystemHealth> getHealth({RequestCancellation? cancellation}) async {
    calls.add('health');
    await _await();
    if (healthError != null) throw healthError!;
    return health!;
  }

  @override
  Future<SchedulerStatus> getSchedulerStatus({
    RequestCancellation? cancellation,
  }) async {
    calls.add('scheduler');
    await _await();
    if (schedulerError != null) throw schedulerError!;
    return scheduler!;
  }

  @override
  Future<DeploymentInfo> getDeployment({
    RequestCancellation? cancellation,
  }) async {
    calls.add('deployment');
    await _await();
    final error = deploymentError;
    if (error != null) {
      if (failDeploymentOnce) deploymentError = null;
      throw error;
    }
    return deployment!;
  }

  @override
  Future<DatabaseInfo> getDatabase({RequestCancellation? cancellation}) async {
    calls.add('database');
    await _await();
    final error = databaseError;
    if (error != null) {
      if (failDatabaseOnce) databaseError = null;
      throw error;
    }
    return database!;
  }

  @override
  Future<ConfigurationInfo> getConfiguration({
    RequestCancellation? cancellation,
  }) async {
    calls.add('configuration');
    await _await();
    if (configurationError != null) throw configurationError!;
    return configuration!;
  }
}

/// Builds a [ProjectDiagnostics] with sensible defaults.
ProjectDiagnostics testProjectDiagnostics({
  String projectId = 'p1',
  ProjectStatus projectStatus = ProjectStatus.implementation,
  Map<String, int>? workItemStatusCounts,
  int activeAgentRuns = 1,
  int staleAgentRuns = 0,
  int attemptCount = 2,
  int repairAttemptCount = 0,
  Duration? executionDuration = const Duration(minutes: 42),
  String? latestTaskId = 'w1',
  String? latestTaskStatus = 'Running',
  String? latestRunId,
  String? latestRunStatus,
  DateTime? lastFailureAtUtc,
  FailureCategory? lastFailureCategory,
  String? lastFailureMessage,
}) => ProjectDiagnostics(
  projectId: projectId,
  projectStatus: projectStatus,
  rawProjectStatus: projectStatus.wireValue,
  repairAttemptCount: repairAttemptCount,
  activeAgentRuns: activeAgentRuns,
  staleAgentRuns: staleAgentRuns,
  attemptCount: attemptCount,
  workItemStatusCounts:
      workItemStatusCounts ??
      const <String, int>{'Completed': 3, 'Running': 1, 'Ready': 2},
  latestTaskId: latestTaskId,
  latestTaskStatus: latestTaskStatus,
  latestRunId: latestRunId,
  latestRunStatus: latestRunStatus,
  executionDuration: executionDuration,
  lastFailureAtUtc: lastFailureAtUtc,
  lastFailureCategory: lastFailureCategory,
  lastFailureMessage: lastFailureMessage,
);

class FakeDiagnosticsRepository implements DiagnosticsRepository {
  AgentMetrics? metrics = testAgentMetrics;
  ProjectDiagnostics? projectDiagnostics = testProjectDiagnostics();

  AppError? metricsError;
  AppError? projectDiagnosticsError;

  /// Completed by the test to control when an in-flight call finishes.
  Future<void>? gate;

  final List<String> calls = <String>[];

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  @override
  Future<AgentMetrics> getAgentMetrics({
    RequestCancellation? cancellation,
  }) async {
    calls.add('agentMetrics');
    await _await();
    if (metricsError != null) throw metricsError!;
    return metrics!;
  }

  @override
  Future<ProjectDiagnostics> getProjectDiagnostics(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    calls.add('projectDiagnostics:$projectId');
    await _await();
    if (projectDiagnosticsError != null) throw projectDiagnosticsError!;
    return projectDiagnostics!;
  }
}

/// Builds an [AuditLogEntry] with sensible defaults.
///
/// [eventType] is the display name the mapper produces from the ordinal;
/// a test that cares about the ordinal sets [rawEventType] as well.
AuditLogEntry testAuditLogEntry({
  String id = 'a1',
  String executionId = 'e1',
  DateTime? timestampUtc,
  String eventType = 'AgentStarted',
  int rawEventType = 3,
  AuditSeverity severity = AuditSeverity.info,
  int? rawSeverity,
  String message = 'Agent started for work item w1.',
  String actorType = 'System',
  String? projectId = 'p1',
  String? workItemId = 'w1',
  String? correlationId = 'corr-1',
  String? outcome,
  String? actorId,
  FailureCategory? failureCategory,
}) => AuditLogEntry(
  id: id,
  executionId: executionId,
  timestampUtc: timestampUtc ?? DateTime.utc(2026, 9, 19, 10),
  eventType: eventType,
  rawEventType: rawEventType,
  severity: severity,
  rawSeverity: rawSeverity ?? severity.index,
  message: message,
  actorType: actorType,
  projectId: projectId,
  workItemId: workItemId,
  correlationId: correlationId,
  outcome: outcome,
  actorId: actorId,
  failureCategory: failureCategory,
);

/// A scripted [AuditRepository].
class FakeAuditRepository implements AuditRepository {
  FakeAuditRepository({List<AuditLogEntry>? entries})
    : entries = entries ?? <AuditLogEntry>[testAuditLogEntry()];

  List<AuditLogEntry> entries;

  AppError? listError;

  /// Completed by the test to control when an in-flight call finishes.
  Future<void>? gate;

  final List<AuditLogQuery> queries = <AuditLogQuery>[];

  int get listCount => queries.length;

  @override
  Future<PagedResult<AuditLogEntry>> list(
    AuditLogQuery query, {
    RequestCancellation? cancellation,
  }) async {
    queries.add(query);
    if (gate != null) await gate;
    if (listError != null) throw listError!;

    final scoped = query.projectId == null
        ? entries
        : entries.where((e) => e.projectId == query.projectId).toList();
    return PagedResult<AuditLogEntry>(
      items: List<AuditLogEntry>.unmodifiable(scoped),
      totalCount: scoped.length,
      page: query.page,
      pageSize: query.pageSize,
    );
  }
}

/// A scripted [ApprovalRepository].
///
/// Records every call, gates on [gate] when set, and throws [approveError]
/// when staged — so a test can assert exactly one approval request was issued
/// even if the controller were to drop the in-flight guard.
class FakeApprovalRepository implements ApprovalRepository {
  /// Staged error thrown by [approve]. Cleared when [failOnce] is true.
  AppError? approveError;
  bool failOnce = false;

  /// Staged result returned by [approve]. When null, returns a `Completed`
  /// copy of [testProject] — the normal server response.
  Project? approveResult;

  /// Completed by the test to observe "button is disabled while pending".
  Future<void>? gate;

  final List<String> calls = <String>[];
  final List<String?> actors = <String?>[];

  int get approveCount => calls.where((c) => c.startsWith('approve:')).length;

  Future<void> _await() async {
    if (gate != null) await gate;
  }

  void _maybeThrow() {
    final error = approveError;
    if (error == null) return;
    if (failOnce) approveError = null;
    throw error;
  }

  @override
  Future<Project> approve(
    String projectId, {
    String? actor,
    RequestCancellation? cancellation,
  }) async {
    calls.add('approve:$projectId');
    actors.add(actor);
    await _await();
    _maybeThrow();
    return approveResult ??
        testProject(id: projectId, status: ProjectStatus.completed);
  }
}
