import 'package:equatable/equatable.dart';

import '../enums/backend_enums.dart';

/// One entry of the backend's operational audit log.
///
/// `GET /api/audit-log` returns the persisted entity as-is, so every field
/// here exists on the wire. Nothing is derived: an entry that carries no
/// actor, no outcome and no failure category is shown with those absent
/// rather than filled in with a plausible default.
class AuditLogEntry extends Equatable {
  const AuditLogEntry({
    required this.id,
    required this.executionId,
    required this.timestampUtc,
    required this.eventType,
    required this.rawEventType,
    required this.severity,
    required this.rawSeverity,
    required this.message,
    required this.actorType,
    this.projectId,
    this.workItemId,
    this.correlationId,
    this.operationId,
    this.parentOperationId,
    this.runId,
    this.outcome,
    this.actorId,
    this.failureCategory,
  });

  final String id;
  final String executionId;
  final DateTime timestampUtc;

  /// The backend event name (`AgentStarted`, `HumanApproval`, …). A technical
  /// identifier, so it is never translated.
  final String eventType;

  /// The ordinal actually received, kept so an event type this client does
  /// not know still round-trips into the UI.
  final int rawEventType;

  /// A log level, never a business result — [outcome] is the business result.
  final AuditSeverity severity;
  final int rawSeverity;

  final String message;

  /// `System`, `Agent`, `Human` or `Anonymous` as the backend recorded it.
  final String actorType;

  final String? projectId;
  final String? workItemId;
  final String? correlationId;
  final String? operationId;
  final String? parentOperationId;
  final String? runId;

  /// Free-form business outcome (`Success`/`Failure`/`Denied`/…). Distinct
  /// from [severity]; the API keeps them apart and so does this client.
  final String? outcome;

  final String? actorId;
  final FailureCategory? failureCategory;

  bool get hasProject => projectId != null;

  @override
  List<Object?> get props => [
    id,
    executionId,
    timestampUtc,
    eventType,
    rawEventType,
    severity,
    rawSeverity,
    message,
    actorType,
    projectId,
    workItemId,
    correlationId,
    operationId,
    parentOperationId,
    runId,
    outcome,
    actorId,
    failureCategory,
  ];
}

/// Filter for `GET /api/audit-log`.
///
/// `severity` is **not** a field here on purpose: the endpoint accepts the
/// parameter, validates it, and then never applies it — the backend's own
/// `AuditLogQuery` carries only project, execution, page and size (gap
/// `G-AUDIT-SEVERITY`). Sending it would imply a server-side filter that does
/// not happen, so severity narrowing is done client-side and labelled as such.
class AuditLogQuery extends Equatable {
  const AuditLogQuery({
    this.projectId,
    this.executionId,
    this.page = 1,
    this.pageSize = 50,
  });

  final String? projectId;
  final String? executionId;
  final int page;

  /// The backend rejects anything above 100.
  final int pageSize;

  Map<String, dynamic> toQueryParameters() => <String, dynamic>{
    if (projectId != null) 'projectId': projectId,
    if (executionId != null) 'executionId': executionId,
    'page': page,
    'pageSize': pageSize,
  };

  @override
  List<Object?> get props => [projectId, executionId, page, pageSize];
}
