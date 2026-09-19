/// DTO -> domain conversion.
///
/// Pure functions, no I/O, no localization: display text is produced later by
/// `EnumLabels`. Every mapper keeps the raw wire value alongside the parsed
/// enum, so a value a newer backend introduces stays visible instead of
/// collapsing into a blank.
library;

import '../../domain/enums/backend_enums.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import '../../domain/models/project.dart';
import '../../domain/models/work_item.dart';
import '../dto/agent_dto.dart';
import '../dto/diagnostics_dto.dart';
import '../dto/operations_dto.dart';
import '../dto/project_dto.dart';
import '../dto/project_plan_dto.dart';
import '../dto/work_item_dto.dart';

Project mapProject(ProjectDto dto) => Project(
  id: dto.id,
  name: dto.name,
  slug: dto.slug,
  description: dto.description,
  status: ProjectStatus.fromWire(dto.status),
  rawStatus: dto.status,
  gitRepository: dto.gitRepository,
  gitBranch: dto.gitBranch,
  lifecycleStartedAtUtc: dto.lifecycleStartedAtUtc,
  currentError: dto.currentError,
  createdAtUtc: dto.createdAtUtc,
  updatedAtUtc: dto.updatedAtUtc,
);

WorkItem mapWorkItem(WorkItemDto dto) => WorkItem(
  id: dto.id,
  projectId: dto.projectId,
  type: WorkItemType.fromWire(dto.type),
  rawType: dto.type,
  title: dto.title,
  description: dto.description,
  status: WorkItemStatus.fromWire(dto.status),
  rawStatus: dto.status,
  priority: WorkItemPriority.fromWire(dto.priority),
  rawPriority: dto.priority,
  attemptCount: dto.attemptCount,
  repairAttemptCount: dto.repairAttemptCount,
  currentError: dto.currentError,
  lastHeartbeatAtUtc: dto.lastHeartbeatAtUtc,
  createdAtUtc: dto.createdAtUtc,
  updatedAtUtc: dto.updatedAtUtc,
);

/// Joins the live work item list with the plan's dependency edges.
///
/// The two endpoints are the only source of each half (gap `G-DEPS`). A plan
/// entry whose work item is missing from [workItems] is skipped rather than
/// rendered from plan data alone, because the plan carries no live state.
ProjectPlan mapProjectPlan(ProjectPlanDto dto, List<WorkItem> workItems) {
  final byId = <String, WorkItem>{for (final item in workItems) item.id: item};
  final workItemIdByPlanKey = <String, String>{
    for (final task in dto.tasks) task.planKey: task.workItemId,
  };

  final nodes = <WorkItemNode>[];
  for (final task in dto.tasks) {
    final workItem = byId[task.workItemId];
    if (workItem == null) continue;

    nodes.add(
      WorkItemNode(
        workItem: workItem,
        planKey: task.planKey,
        dependsOnPlanKeys: List<String>.unmodifiable(task.dependsOn),
        dependsOnWorkItemIds: List<String>.unmodifiable(
          task.dependsOn
              .map((key) => workItemIdByPlanKey[key])
              .whereType<String>(),
        ),
        readiness: TaskReadiness.fromWire(task.readiness),
      ),
    );
  }

  return ProjectPlan(
    projectId: dto.projectId,
    planHash: dto.planHash,
    nodes: List<WorkItemNode>.unmodifiable(nodes),
  );
}

AgentDefinition mapAgentDefinition(AgentDefinitionDto dto) => AgentDefinition(
  id: dto.id,
  name: dto.name,
  description: dto.description,
  agentType: WorkItemType.fromWire(dto.agentType),
  rawAgentType: dto.agentType,
  enabled: dto.enabled,
  createdAtUtc: dto.createdAtUtc,
  updatedAtUtc: dto.updatedAtUtc,
);

AgentRun mapAgentRun(AgentRunDto dto) => AgentRun(
  id: dto.id,
  projectId: dto.projectId,
  workItemId: dto.workItemId,
  agentDefinitionId: dto.agentDefinitionId,
  status: AgentRunStatus.fromWire(dto.status),
  rawStatus: dto.status,
  attempt: dto.attempt,
  createdAtUtc: dto.createdAtUtc,
  startedAtUtc: dto.startedAtUtc,
  completedAtUtc: dto.completedAtUtc,
  duration: dto.durationMs == null
      ? null
      : Duration(milliseconds: dto.durationMs!),
  output: dto.output,
  failureCategory: dto.failureCategory == null
      ? null
      : FailureCategory.fromWire(dto.failureCategory),
  rawFailureCategory: dto.failureCategory,
  errorMessage: dto.errorMessage,
  correlationId: dto.correlationId,
  structuredResult: dto.structuredResult == null
      ? null
      : mapStructuredAgentResult(dto.structuredResult!),
);

StructuredAgentResult mapStructuredAgentResult(StructuredAgentResultDto dto) =>
    StructuredAgentResult(
      version: dto.version,
      status: StructuredResultStatus.fromWire(dto.status),
      rawStatus: dto.status,
      summary: dto.summary,
      output: dto.output,
      data: dto.data,
      warnings: List<String>.unmodifiable(dto.warnings),
      errors: List<String>.unmodifiable(dto.errors),
      artifacts: List<String>.unmodifiable(dto.artifacts),
      metadata: dto.metadata == null
          ? null
          : Map<String, String>.unmodifiable(dto.metadata!),
    );

AgentMetrics mapAgentMetrics(AgentMetricsDto dto) => AgentMetrics(
  queued: dto.queued,
  running: dto.running,
  created: dto.created,
  started: dto.started,
  succeeded: dto.succeeded,
  failed: dto.failed,
  timedOut: dto.timedOut,
  cancelled: dto.cancelled,
  workerClaimSuccess: dto.workerClaimSuccess,
  averageDuration: dto.averageDurationMs == null
      ? null
      : Duration(microseconds: (dto.averageDurationMs! * 1000).round()),
  totalDuration: dto.totalDurationMs == null
      ? null
      : Duration(milliseconds: dto.totalDurationMs!),
);

ProjectDiagnostics mapProjectDiagnostics(ProjectDiagnosticsDto dto) =>
    ProjectDiagnostics(
      projectId: dto.projectId,
      projectStatus: ProjectStatus.fromWire(dto.projectStatus),
      rawProjectStatus: dto.projectStatus,
      latestTaskId: dto.latestTaskId,
      latestTaskStatus: dto.latestTaskStatus,
      repairAttemptCount: dto.repairAttemptCount,
      latestRunId: dto.latestRunId,
      latestRunStatus: dto.latestRunStatus,
      lastSuccessAtUtc: dto.lastSuccessAtUtc,
      lastFailureAtUtc: dto.lastFailureAtUtc,
      lastFailureCategory: mapFailureCategoryValue(dto.lastFailureCategory),
      lastFailureMessage: dto.lastFailureMessage,
      latestApprovalDecision: mapApprovalDecisionValue(
        dto.latestApprovalDecision,
      ),
      workItemStatusCounts: Map<String, int>.unmodifiable(
        dto.workItemStatusCounts ?? const <String, int>{},
      ),
      activeAgentRuns: dto.activeAgentRuns,
      staleAgentRuns: dto.staleAgentRuns,
      executionDuration: dto.executionDurationMs == null
          ? null
          : Duration(milliseconds: dto.executionDurationMs!),
      attemptCount: dto.attemptCount,
    );

/// The diagnostics endpoint sends its enums as ordinals today, because the
/// API registers no `JsonStringEnumConverter` for raw record responses. Both
/// forms are accepted so the client keeps working if that ever changes.
FailureCategory? mapFailureCategoryValue(Object? value) => switch (value) {
  null => null,
  final int ordinal => FailureCategory.fromOrdinal(ordinal),
  final String name => FailureCategory.fromWire(name),
  _ => FailureCategory.unknown,
};

ApprovalDecision? mapApprovalDecisionValue(Object? value) => switch (value) {
  null => null,
  final int ordinal => ApprovalDecision.fromOrdinal(ordinal),
  final String name => ApprovalDecision.fromWire(name),
  _ => ApprovalDecision.unknown,
};

DeploymentInfo mapDeploymentInfo(DeploymentInfoDto dto) => DeploymentInfo(
  applicationName: dto.applicationName,
  environment: dto.environment,
  version: dto.version,
  informationalVersion: dto.informationalVersion,
  frameworkDescription: dto.frameworkDescription,
  osDescription: dto.osDescription,
  processArchitecture: dto.processArchitecture,
  isContainerized: dto.isContainerized,
  startedAtUtc: dto.startedAtUtc,
  uptime: dto.uptime,
  databaseProvider: dto.databaseProvider,
  gitCommit: dto.gitCommit,
  gitBranch: dto.gitBranch,
  buildNumber: dto.buildNumber,
  buildTimestamp: dto.buildTimestamp,
);

DatabaseInfo mapDatabaseInfo(DatabaseInfoDto dto) => DatabaseInfo(
  generatedAtUtc: dto.generatedAtUtc,
  providerName: dto.providerName,
  isReady: dto.isReady,
  commandTimeoutSeconds: dto.commandTimeoutSeconds,
  retriesEnabled: dto.retriesEnabled,
  maxRetryAttempts: dto.maxRetryAttempts,
  optimisticConcurrencyEnabled: dto.optimisticConcurrencyEnabled,
  operationsCount: dto.operationsCount,
  failuresCount: dto.failuresCount,
  retriesCount: dto.retriesCount,
  transientFailuresCount: dto.transientFailuresCount,
  concurrencyConflictsCount: dto.concurrencyConflictsCount,
  busyFailuresCount: dto.busyFailuresCount,
  lockedFailuresCount: dto.lockedFailuresCount,
  transactionFailuresCount: dto.transactionFailuresCount,
  averageDurationMs: dto.averageDurationMs,
);

ConfigurationInfo mapConfigurationInfo(ConfigurationInfoDto dto) =>
    ConfigurationInfo(
      generatedAtUtc: dto.generatedAtUtc,
      environmentName: dto.environmentName,
      isValid: dto.isValid,
      databaseProvider: dto.database.provider,
      databaseConfigured: dto.database.configured,
      secretManagementEnabled: dto.secretManagement.enabled,
      secretProviderName: dto.secretManagement.providerName,
      operationalEndpointsSecured: dto.security.operationalEndpointsSecured,
      operationalKeyHeader: dto.security.operationalKeyHeader,
      rateLimitingEnabled: dto.security.rateLimitingEnabled,
      maxRequestBodySizeBytes: dto.security.maxRequestBodySizeBytes,
      corsEnabled: dto.security.corsEnabled,
      httpsRedirectEnabled: dto.security.httpsRedirectEnabled,
      hstsEnabled: dto.security.hstsEnabled,
      resilienceProfileNames: List<String>.unmodifiable(
        dto.resilience.profileNames,
      ),
      providers: List<ProviderInfo>.unmodifiable(
        dto.externalProviders.map(
          (p) => ProviderInfo(
            name: p.name,
            enabled: p.enabled,
            hasSecretReference: p.hasSecretReference,
            secretReferenceName: p.secretReferenceName,
          ),
        ),
      ),
      configurationSources: List<String>.unmodifiable(dto.configurationSources),
    );

SchedulerStatus mapSchedulerStatus(SchedulerStatusDto dto) => SchedulerStatus(
  enabled: dto.enabled,
  maxConcurrentTasks: dto.maxConcurrentTasks,
  lastCycleAtUtc: dto.lastCycleAtUtc,
  lastCycleProjectId: dto.lastCycleProjectId,
  lastCycleDispatchedCount: dto.lastCycleDispatchedCount,
  lastCycleFailureCount: dto.lastCycleFailureCount,
  lastCycleSkippedDueToConcurrency: dto.lastCycleSkippedDueToConcurrency,
  totalDispatched: dto.totalDispatched,
  totalFailures: dto.totalFailures,
);
