import 'package:equatable/equatable.dart';

import '../enums/backend_enums.dart';

/// Operator-facing read model for one project's execution state.
class ProjectDiagnostics extends Equatable {
  const ProjectDiagnostics({
    required this.projectId,
    required this.projectStatus,
    required this.rawProjectStatus,
    required this.repairAttemptCount,
    required this.activeAgentRuns,
    required this.staleAgentRuns,
    required this.attemptCount,
    required this.workItemStatusCounts,
    this.latestTaskId,
    this.latestTaskStatus,
    this.latestRunId,
    this.latestRunStatus,
    this.lastSuccessAtUtc,
    this.lastFailureAtUtc,
    this.lastFailureCategory,
    this.lastFailureMessage,
    this.latestApprovalDecision,
    this.executionDuration,
  });

  final String projectId;
  final ProjectStatus projectStatus;
  final String rawProjectStatus;

  final String? latestTaskId;
  final String? latestTaskStatus;
  final int repairAttemptCount;

  /// The only way to discover an orchestration run id: there is no list
  /// endpoint for them (gap `G-RUN-LIST`).
  final String? latestRunId;

  final String? latestRunStatus;
  final DateTime? lastSuccessAtUtc;
  final DateTime? lastFailureAtUtc;
  final FailureCategory? lastFailureCategory;
  final String? lastFailureMessage;
  final ApprovalDecision? latestApprovalDecision;

  /// Raw work item status string -> count.
  final Map<String, int> workItemStatusCounts;

  final int activeAgentRuns;

  /// Runs that stopped reporting a heartbeat. Per project only.
  final int staleAgentRuns;

  final Duration? executionDuration;
  final int attemptCount;

  int get totalWorkItems =>
      workItemStatusCounts.values.fold(0, (sum, count) => sum + count);

  int workItemsWithStatus(WorkItemStatus status) =>
      workItemStatusCounts[status.wireValue] ?? 0;

  bool get hasRecentFailure => lastFailureAtUtc != null;

  @override
  List<Object?> get props => [
    projectId,
    projectStatus,
    rawProjectStatus,
    latestTaskId,
    latestTaskStatus,
    repairAttemptCount,
    latestRunId,
    latestRunStatus,
    lastSuccessAtUtc,
    lastFailureAtUtc,
    lastFailureCategory,
    lastFailureMessage,
    latestApprovalDecision,
    workItemStatusCounts,
    activeAgentRuns,
    staleAgentRuns,
    executionDuration,
    attemptCount,
  ];
}

/// Version and runtime metadata of the API this client is talking to.
class DeploymentInfo extends Equatable {
  const DeploymentInfo({
    required this.applicationName,
    required this.environment,
    required this.version,
    required this.informationalVersion,
    required this.frameworkDescription,
    required this.osDescription,
    required this.processArchitecture,
    required this.isContainerized,
    required this.startedAtUtc,
    required this.databaseProvider,
    this.uptime,
    this.gitCommit,
    this.gitBranch,
    this.buildNumber,
    this.buildTimestamp,
  });

  final String applicationName;
  final String environment;
  final String version;
  final String informationalVersion;
  final String frameworkDescription;
  final String osDescription;
  final String processArchitecture;
  final bool isContainerized;
  final DateTime startedAtUtc;
  final Duration? uptime;
  final String databaseProvider;
  final String? gitCommit;
  final String? gitBranch;
  final String? buildNumber;
  final String? buildTimestamp;

  @override
  List<Object?> get props => [
    applicationName,
    environment,
    version,
    informationalVersion,
    frameworkDescription,
    osDescription,
    processArchitecture,
    isContainerized,
    startedAtUtc,
    uptime,
    databaseProvider,
    gitCommit,
    gitBranch,
    buildNumber,
    buildTimestamp,
  ];
}

/// Database health and resilience counters. Never a connection string.
class DatabaseInfo extends Equatable {
  const DatabaseInfo({
    required this.generatedAtUtc,
    required this.providerName,
    required this.isReady,
    required this.commandTimeoutSeconds,
    required this.retriesEnabled,
    required this.maxRetryAttempts,
    required this.optimisticConcurrencyEnabled,
    required this.operationsCount,
    required this.failuresCount,
    required this.retriesCount,
    required this.transientFailuresCount,
    required this.concurrencyConflictsCount,
    required this.busyFailuresCount,
    required this.lockedFailuresCount,
    required this.transactionFailuresCount,
    required this.averageDurationMs,
  });

  final DateTime generatedAtUtc;
  final String providerName;
  final bool isReady;
  final int commandTimeoutSeconds;
  final bool retriesEnabled;
  final int maxRetryAttempts;
  final bool optimisticConcurrencyEnabled;
  final int operationsCount;
  final int failuresCount;
  final int retriesCount;
  final int transientFailuresCount;
  final int concurrencyConflictsCount;
  final int busyFailuresCount;
  final int lockedFailuresCount;
  final int transactionFailuresCount;
  final double averageDurationMs;

  bool get hasFailures => failuresCount > 0;

  @override
  List<Object?> get props => [
    generatedAtUtc,
    providerName,
    isReady,
    commandTimeoutSeconds,
    retriesEnabled,
    maxRetryAttempts,
    optimisticConcurrencyEnabled,
    operationsCount,
    failuresCount,
    retriesCount,
    transientFailuresCount,
    concurrencyConflictsCount,
    busyFailuresCount,
    lockedFailuresCount,
    transactionFailuresCount,
    averageDurationMs,
  ];
}

/// Sanitized configuration metadata: flags, names and counts only.
class ConfigurationInfo extends Equatable {
  const ConfigurationInfo({
    required this.generatedAtUtc,
    required this.environmentName,
    required this.isValid,
    required this.databaseProvider,
    required this.databaseConfigured,
    required this.secretManagementEnabled,
    required this.secretProviderName,
    required this.operationalEndpointsSecured,
    required this.operationalKeyHeader,
    required this.rateLimitingEnabled,
    required this.maxRequestBodySizeBytes,
    required this.corsEnabled,
    required this.httpsRedirectEnabled,
    required this.hstsEnabled,
    required this.resilienceProfileNames,
    required this.providers,
    required this.configurationSources,
  });

  final DateTime generatedAtUtc;
  final String environmentName;
  final bool isValid;
  final String databaseProvider;
  final bool databaseConfigured;
  final bool secretManagementEnabled;
  final String secretProviderName;
  final bool operationalEndpointsSecured;

  /// The header name the backend expects for operational calls. Knowing the
  /// name is not knowing the key.
  final String operationalKeyHeader;

  final bool rateLimitingEnabled;
  final int maxRequestBodySizeBytes;
  final bool corsEnabled;
  final bool httpsRedirectEnabled;
  final bool hstsEnabled;
  final List<String> resilienceProfileNames;
  final List<ProviderInfo> providers;
  final List<String> configurationSources;

  @override
  List<Object?> get props => [
    generatedAtUtc,
    environmentName,
    isValid,
    databaseProvider,
    databaseConfigured,
    secretManagementEnabled,
    secretProviderName,
    operationalEndpointsSecured,
    operationalKeyHeader,
    rateLimitingEnabled,
    maxRequestBodySizeBytes,
    corsEnabled,
    httpsRedirectEnabled,
    hstsEnabled,
    resilienceProfileNames,
    providers,
    configurationSources,
  ];
}

/// An external AI provider as the API describes it: name and flags, never a
/// key, an endpoint credential or a secret value.
class ProviderInfo extends Equatable {
  const ProviderInfo({
    required this.name,
    required this.enabled,
    required this.hasSecretReference,
    this.secretReferenceName,
  });

  final String name;
  final bool enabled;
  final bool hasSecretReference;
  final String? secretReferenceName;

  @override
  List<Object?> get props => [
    name,
    enabled,
    hasSecretReference,
    secretReferenceName,
  ];
}

/// Task scheduler activity. The nearest available signal for "is the Worker
/// doing anything", since worker health itself is not exposed
/// (gap `G-WORKER-HEALTH`).
class SchedulerStatus extends Equatable {
  const SchedulerStatus({
    required this.enabled,
    required this.maxConcurrentTasks,
    required this.lastCycleDispatchedCount,
    required this.lastCycleFailureCount,
    required this.lastCycleSkippedDueToConcurrency,
    required this.totalDispatched,
    required this.totalFailures,
    this.lastCycleAtUtc,
    this.lastCycleProjectId,
  });

  final bool enabled;
  final int maxConcurrentTasks;
  final DateTime? lastCycleAtUtc;
  final String? lastCycleProjectId;
  final int lastCycleDispatchedCount;
  final int lastCycleFailureCount;
  final int lastCycleSkippedDueToConcurrency;
  final int totalDispatched;
  final int totalFailures;

  /// A scheduler that has never run a cycle is not evidence of a healthy
  /// worker; the caller must present this as activity, not as health.
  bool get hasRunACycle => lastCycleAtUtc != null;

  @override
  List<Object?> get props => [
    enabled,
    maxConcurrentTasks,
    lastCycleAtUtc,
    lastCycleProjectId,
    lastCycleDispatchedCount,
    lastCycleFailureCount,
    lastCycleSkippedDueToConcurrency,
    totalDispatched,
    totalFailures,
  ];
}

/// Result of the two health probes, which return plain text rather than JSON
/// and name no individual check (gap `G-HEALTH-DETAIL`).
class SystemHealth extends Equatable {
  const SystemHealth({
    required this.isAlive,
    required this.isReady,
    required this.checkedAtUtc,
  });

  const SystemHealth.unreachable(this.checkedAtUtc)
    : isAlive = false,
      isReady = false;

  /// `/health/live` answered.
  final bool isAlive;

  /// `/health/ready` reported `Healthy`.
  final bool isReady;

  final DateTime checkedAtUtc;

  bool get isDegraded => isAlive && !isReady;

  @override
  List<Object?> get props => [isAlive, isReady, checkedAtUtc];
}
