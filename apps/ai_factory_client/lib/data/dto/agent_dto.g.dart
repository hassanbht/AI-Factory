// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'agent_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

AgentDefinitionDto _$AgentDefinitionDtoFromJson(Map<String, dynamic> json) =>
    AgentDefinitionDto(
      id: json['id'] as String,
      name: json['name'] as String,
      agentType: json['agentType'] as String,
      enabled: json['enabled'] as bool,
      createdAtUtc: const UtcDateTimeConverter().fromJson(
        json['createdAtUtc'] as String,
      ),
      updatedAtUtc: const UtcDateTimeConverter().fromJson(
        json['updatedAtUtc'] as String,
      ),
      description: json['description'] as String?,
    );

Map<String, dynamic> _$AgentDefinitionDtoToJson(
  AgentDefinitionDto instance,
) => <String, dynamic>{
  'id': instance.id,
  'name': instance.name,
  'description': instance.description,
  'agentType': instance.agentType,
  'enabled': instance.enabled,
  'createdAtUtc': const UtcDateTimeConverter().toJson(instance.createdAtUtc),
  'updatedAtUtc': const UtcDateTimeConverter().toJson(instance.updatedAtUtc),
};

AgentRunDto _$AgentRunDtoFromJson(Map<String, dynamic> json) => AgentRunDto(
  id: json['id'] as String,
  agentDefinitionId: json['agentDefinitionId'] as String,
  status: json['status'] as String,
  attempt: (json['attempt'] as num).toInt(),
  createdAtUtc: const UtcDateTimeConverter().fromJson(
    json['createdAtUtc'] as String,
  ),
  correlationId: json['correlationId'] as String,
  projectId: json['projectId'] as String?,
  workItemId: json['workItemId'] as String?,
  startedAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['startedAtUtc'] as String?,
  ),
  completedAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['completedAtUtc'] as String?,
  ),
  durationMs: (json['durationMs'] as num?)?.toInt(),
  output: json['output'] as String?,
  failureCategory: json['failureCategory'] as String?,
  errorMessage: json['errorMessage'] as String?,
  structuredResult: json['structuredResult'] == null
      ? null
      : StructuredAgentResultDto.fromJson(
          json['structuredResult'] as Map<String, dynamic>,
        ),
);

Map<String, dynamic> _$AgentRunDtoToJson(
  AgentRunDto instance,
) => <String, dynamic>{
  'id': instance.id,
  'projectId': instance.projectId,
  'workItemId': instance.workItemId,
  'agentDefinitionId': instance.agentDefinitionId,
  'status': instance.status,
  'attempt': instance.attempt,
  'createdAtUtc': const UtcDateTimeConverter().toJson(instance.createdAtUtc),
  'startedAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.startedAtUtc,
  ),
  'completedAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.completedAtUtc,
  ),
  'durationMs': instance.durationMs,
  'output': instance.output,
  'failureCategory': instance.failureCategory,
  'errorMessage': instance.errorMessage,
  'correlationId': instance.correlationId,
  'structuredResult': instance.structuredResult,
};

StructuredAgentResultDto _$StructuredAgentResultDtoFromJson(
  Map<String, dynamic> json,
) => StructuredAgentResultDto(
  version: (json['version'] as num).toInt(),
  status: json['status'] as String,
  summary: json['summary'] as String?,
  output: json['output'] as String?,
  data: json['data'] as String?,
  warnings:
      (json['warnings'] as List<dynamic>?)?.map((e) => e as String).toList() ??
      [],
  errors:
      (json['errors'] as List<dynamic>?)?.map((e) => e as String).toList() ??
      [],
  artifacts:
      (json['artifacts'] as List<dynamic>?)?.map((e) => e as String).toList() ??
      [],
  metadata: (json['metadata'] as Map<String, dynamic>?)?.map(
    (k, e) => MapEntry(k, e as String),
  ),
);

Map<String, dynamic> _$StructuredAgentResultDtoToJson(
  StructuredAgentResultDto instance,
) => <String, dynamic>{
  'version': instance.version,
  'status': instance.status,
  'summary': instance.summary,
  'output': instance.output,
  'data': instance.data,
  'warnings': instance.warnings,
  'errors': instance.errors,
  'artifacts': instance.artifacts,
  'metadata': instance.metadata,
};

AgentRunPageDto _$AgentRunPageDtoFromJson(Map<String, dynamic> json) =>
    AgentRunPageDto(
      items: (json['items'] as List<dynamic>)
          .map((e) => AgentRunDto.fromJson(e as Map<String, dynamic>))
          .toList(),
      totalCount: (json['totalCount'] as num).toInt(),
      page: (json['page'] as num).toInt(),
      pageSize: (json['pageSize'] as num).toInt(),
    );

Map<String, dynamic> _$AgentRunPageDtoToJson(AgentRunPageDto instance) =>
    <String, dynamic>{
      'items': instance.items,
      'totalCount': instance.totalCount,
      'page': instance.page,
      'pageSize': instance.pageSize,
    };

AgentMetricsDto _$AgentMetricsDtoFromJson(Map<String, dynamic> json) =>
    AgentMetricsDto(
      queued: (json['queued'] as num).toInt(),
      running: (json['running'] as num).toInt(),
      created: (json['created'] as num).toInt(),
      started: (json['started'] as num).toInt(),
      succeeded: (json['succeeded'] as num).toInt(),
      failed: (json['failed'] as num).toInt(),
      timedOut: (json['timedOut'] as num).toInt(),
      cancelled: (json['cancelled'] as num).toInt(),
      workerClaimSuccess: (json['workerClaimSuccess'] as num).toInt(),
      averageDurationMs: (json['averageDurationMs'] as num?)?.toDouble(),
      totalDurationMs: (json['totalDurationMs'] as num?)?.toInt(),
    );

Map<String, dynamic> _$AgentMetricsDtoToJson(AgentMetricsDto instance) =>
    <String, dynamic>{
      'queued': instance.queued,
      'running': instance.running,
      'created': instance.created,
      'started': instance.started,
      'succeeded': instance.succeeded,
      'failed': instance.failed,
      'timedOut': instance.timedOut,
      'cancelled': instance.cancelled,
      'workerClaimSuccess': instance.workerClaimSuccess,
      'averageDurationMs': instance.averageDurationMs,
      'totalDurationMs': instance.totalDurationMs,
    };
