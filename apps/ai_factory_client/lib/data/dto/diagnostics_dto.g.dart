// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'diagnostics_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

ProjectDiagnosticsDto _$ProjectDiagnosticsDtoFromJson(
  Map<String, dynamic> json,
) => ProjectDiagnosticsDto(
  projectId: json['projectId'] as String,
  projectStatus: json['projectStatus'] as String,
  repairAttemptCount: (json['repairAttemptCount'] as num).toInt(),
  activeAgentRuns: (json['activeAgentRuns'] as num).toInt(),
  staleAgentRuns: (json['staleAgentRuns'] as num).toInt(),
  attemptCount: (json['attemptCount'] as num).toInt(),
  latestTaskId: json['latestTaskId'] as String?,
  latestTaskStatus: json['latestTaskStatus'] as String?,
  latestRunId: json['latestRunId'] as String?,
  latestRunStatus: json['latestRunStatus'] as String?,
  lastSuccessAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['lastSuccessAtUtc'] as String?,
  ),
  lastFailureAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['lastFailureAtUtc'] as String?,
  ),
  lastFailureCategory: json['lastFailureCategory'],
  lastFailureMessage: json['lastFailureMessage'] as String?,
  latestApprovalDecision: json['latestApprovalDecision'],
  workItemStatusCounts: (json['workItemStatusCounts'] as Map<String, dynamic>?)
      ?.map((k, e) => MapEntry(k, (e as num).toInt())),
  executionDurationMs: (json['executionDurationMs'] as num?)?.toInt(),
);

Map<String, dynamic> _$ProjectDiagnosticsDtoToJson(
  ProjectDiagnosticsDto instance,
) => <String, dynamic>{
  'projectId': instance.projectId,
  'projectStatus': instance.projectStatus,
  'latestTaskId': instance.latestTaskId,
  'latestTaskStatus': instance.latestTaskStatus,
  'repairAttemptCount': instance.repairAttemptCount,
  'latestRunId': instance.latestRunId,
  'latestRunStatus': instance.latestRunStatus,
  'lastSuccessAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.lastSuccessAtUtc,
  ),
  'lastFailureAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.lastFailureAtUtc,
  ),
  'lastFailureCategory': instance.lastFailureCategory,
  'lastFailureMessage': instance.lastFailureMessage,
  'latestApprovalDecision': instance.latestApprovalDecision,
  'workItemStatusCounts': instance.workItemStatusCounts,
  'activeAgentRuns': instance.activeAgentRuns,
  'staleAgentRuns': instance.staleAgentRuns,
  'executionDurationMs': instance.executionDurationMs,
  'attemptCount': instance.attemptCount,
};
