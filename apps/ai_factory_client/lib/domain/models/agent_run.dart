import 'package:equatable/equatable.dart';

import '../enums/backend_enums.dart';

/// A registered agent the Factory can dispatch work to.
class AgentDefinition extends Equatable {
  const AgentDefinition({
    required this.id,
    required this.name,
    required this.agentType,
    required this.rawAgentType,
    required this.enabled,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
  });

  final String id;
  final String name;
  final String? description;

  final WorkItemType agentType;
  final String rawAgentType;

  final bool enabled;
  final DateTime createdAtUtc;
  final DateTime updatedAtUtc;

  @override
  List<Object?> get props => [
    id,
    name,
    description,
    agentType,
    rawAgentType,
    enabled,
    createdAtUtc,
    updatedAtUtc,
  ];
}

/// One execution of an agent.
///
/// The model deliberately has **no** `model`, `provider`, `fallbackUsed` or
/// token fields: the API does not return them (gaps `G-RUN-TELEMETRY` and
/// `G-FALLBACK`). A nullable field here would be indistinguishable from
/// "the backend said there was none", so the field does not exist at all and
/// the UI states that the API does not expose it.
class AgentRun extends Equatable {
  const AgentRun({
    required this.id,
    required this.agentDefinitionId,
    required this.status,
    required this.rawStatus,
    required this.attempt,
    required this.createdAtUtc,
    required this.correlationId,
    this.projectId,
    this.workItemId,
    this.startedAtUtc,
    this.completedAtUtc,
    this.duration,
    this.output,
    this.failureCategory,
    this.rawFailureCategory,
    this.errorMessage,
    this.structuredResult,
  });

  final String id;
  final String? projectId;
  final String? workItemId;
  final String agentDefinitionId;

  final AgentRunStatus status;
  final String rawStatus;

  final int attempt;

  final DateTime createdAtUtc;
  final DateTime? startedAtUtc;
  final DateTime? completedAtUtc;

  /// Server-computed; null until the run both started and completed.
  final Duration? duration;

  final String? output;

  final FailureCategory? failureCategory;
  final String? rawFailureCategory;

  final String? errorMessage;

  /// The join key into `/api/operations/{correlationId}` and the audit log.
  final String correlationId;

  final StructuredAgentResult? structuredResult;

  bool get isTerminal => status.isTerminal;

  bool get isRunning =>
      status == AgentRunStatus.running || status == AgentRunStatus.waiting;

  bool get hasFailed =>
      status == AgentRunStatus.failed || status == AgentRunStatus.timedOut;

  @override
  List<Object?> get props => [
    id,
    projectId,
    workItemId,
    agentDefinitionId,
    status,
    rawStatus,
    attempt,
    createdAtUtc,
    startedAtUtc,
    completedAtUtc,
    duration,
    output,
    failureCategory,
    rawFailureCategory,
    errorMessage,
    correlationId,
    structuredResult,
  ];
}

/// The agreed result envelope an agent returns.
class StructuredAgentResult extends Equatable {
  const StructuredAgentResult({
    required this.version,
    required this.status,
    required this.rawStatus,
    required this.warnings,
    required this.errors,
    required this.artifacts,
    this.summary,
    this.output,
    this.data,
    this.metadata,
  });

  final int version;
  final StructuredResultStatus status;
  final String rawStatus;

  final String? summary;
  final String? output;

  /// Agent-produced JSON, carried opaquely: the client does not interpret it.
  final String? data;

  final List<String> warnings;
  final List<String> errors;

  /// Artifact **names** only. There is no artifact endpoint, so these cannot
  /// be opened or diffed (gap `G-ARTIFACTS`).
  final List<String> artifacts;

  final Map<String, String>? metadata;

  bool get hasWarnings => warnings.isNotEmpty;

  bool get hasErrors => errors.isNotEmpty;

  @override
  List<Object?> get props => [
    version,
    status,
    rawStatus,
    summary,
    output,
    data,
    warnings,
    errors,
    artifacts,
    metadata,
  ];
}

/// Global agent execution counters. They reset when the API restarts and are
/// not scoped to a project.
class AgentMetrics extends Equatable {
  const AgentMetrics({
    required this.queued,
    required this.running,
    required this.created,
    required this.started,
    required this.succeeded,
    required this.failed,
    required this.timedOut,
    required this.cancelled,
    required this.workerClaimSuccess,
    this.averageDuration,
    this.totalDuration,
  });

  final int queued;
  final int running;
  final int created;
  final int started;
  final int succeeded;
  final int failed;
  final int timedOut;
  final int cancelled;
  final int workerClaimSuccess;
  final Duration? averageDuration;
  final Duration? totalDuration;

  int get inFlight => queued + running;

  @override
  List<Object?> get props => [
    queued,
    running,
    created,
    started,
    succeeded,
    failed,
    timedOut,
    cancelled,
    workerClaimSuccess,
    averageDuration,
    totalDuration,
  ];
}

/// One page of a paged API listing.
class PagedResult<T> extends Equatable {
  const PagedResult({
    required this.items,
    required this.totalCount,
    required this.page,
    required this.pageSize,
  });

  final List<T> items;
  final int totalCount;
  final int page;
  final int pageSize;

  bool get hasMore => page * pageSize < totalCount;

  @override
  List<Object?> get props => [items, totalCount, page, pageSize];
}
