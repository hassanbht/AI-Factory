// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'audit_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

AuditLogEntryDto _$AuditLogEntryDtoFromJson(Map<String, dynamic> json) =>
    AuditLogEntryDto(
      id: json['id'] as String,
      executionId: json['executionId'] as String,
      timestampUtc: const UtcDateTimeConverter().fromJson(
        json['timestampUtc'] as String,
      ),
      eventType: (json['eventType'] as num).toInt(),
      severity: (json['severity'] as num).toInt(),
      message: json['message'] as String,
      actorType: json['actorType'] as String,
      projectId: json['projectId'] as String?,
      workItemId: json['workItemId'] as String?,
      correlationId: json['correlationId'] as String?,
      operationId: json['operationId'] as String?,
      parentOperationId: json['parentOperationId'] as String?,
      runId: json['runId'] as String?,
      outcome: json['outcome'] as String?,
      actorId: json['actorId'] as String?,
      failureCategory: (json['failureCategory'] as num?)?.toInt(),
    );

AuditLogPageDto _$AuditLogPageDtoFromJson(Map<String, dynamic> json) =>
    AuditLogPageDto(
      items: (json['items'] as List<dynamic>)
          .map((e) => AuditLogEntryDto.fromJson(e as Map<String, dynamic>))
          .toList(),
      totalCount: (json['totalCount'] as num).toInt(),
      page: (json['page'] as num).toInt(),
      pageSize: (json['pageSize'] as num).toInt(),
    );
