import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'audit_dto.g.dart';

/// One item of `GET /api/audit-log`.
///
/// The endpoint returns the persisted `AuditLogEntry` entity directly and the
/// API registers no `JsonStringEnumConverter`, so `eventType`, `severity` and
/// `failureCategory` arrive as **integer ordinals** rather than names — the
/// same wire shape the project diagnostics endpoint uses.
///
/// `metadataJson` exists on the wire and is deliberately not declared here.
/// It is a free-form blob written by many call sites; the backend filters
/// credentials out of it on write, but rendering it would mean trusting that
/// filter with whatever a future call site adds. Not reading it is the
/// cheaper guarantee (Workstream Q).
@JsonSerializable(createToJson: false)
class AuditLogEntryDto {
  const AuditLogEntryDto({
    required this.id,
    required this.executionId,
    required this.timestampUtc,
    required this.eventType,
    required this.severity,
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

  factory AuditLogEntryDto.fromJson(Map<String, dynamic> json) =>
      _$AuditLogEntryDtoFromJson(json);

  final String id;
  final String executionId;

  @UtcDateTimeConverter()
  final DateTime timestampUtc;

  final int eventType;
  final int severity;
  final String message;
  final String actorType;

  final String? projectId;
  final String? workItemId;
  final String? correlationId;
  final String? operationId;
  final String? parentOperationId;
  final String? runId;
  final String? outcome;
  final String? actorId;
  final int? failureCategory;
}

/// `GET /api/audit-log` — the backend's `AuditLogPage`.
@JsonSerializable(createToJson: false)
class AuditLogPageDto {
  const AuditLogPageDto({
    required this.items,
    required this.totalCount,
    required this.page,
    required this.pageSize,
  });

  factory AuditLogPageDto.fromJson(Map<String, dynamic> json) =>
      _$AuditLogPageDtoFromJson(json);

  final List<AuditLogEntryDto> items;
  final int totalCount;
  final int page;
  final int pageSize;
}
