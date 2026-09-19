// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'work_item_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

WorkItemDto _$WorkItemDtoFromJson(Map<String, dynamic> json) => WorkItemDto(
  id: json['id'] as String,
  projectId: json['projectId'] as String,
  type: json['type'] as String,
  title: json['title'] as String,
  status: json['status'] as String,
  priority: json['priority'] as String,
  attemptCount: (json['attemptCount'] as num).toInt(),
  repairAttemptCount: (json['repairAttemptCount'] as num).toInt(),
  createdAtUtc: const UtcDateTimeConverter().fromJson(
    json['createdAtUtc'] as String,
  ),
  updatedAtUtc: const UtcDateTimeConverter().fromJson(
    json['updatedAtUtc'] as String,
  ),
  description: json['description'] as String?,
  currentError: json['currentError'] as String?,
  lastHeartbeatAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['lastHeartbeatAtUtc'] as String?,
  ),
);

Map<String, dynamic> _$WorkItemDtoToJson(
  WorkItemDto instance,
) => <String, dynamic>{
  'id': instance.id,
  'projectId': instance.projectId,
  'type': instance.type,
  'title': instance.title,
  'description': instance.description,
  'status': instance.status,
  'priority': instance.priority,
  'attemptCount': instance.attemptCount,
  'repairAttemptCount': instance.repairAttemptCount,
  'currentError': instance.currentError,
  'lastHeartbeatAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.lastHeartbeatAtUtc,
  ),
  'createdAtUtc': const UtcDateTimeConverter().toJson(instance.createdAtUtc),
  'updatedAtUtc': const UtcDateTimeConverter().toJson(instance.updatedAtUtc),
};

TaskReadinessDto _$TaskReadinessDtoFromJson(Map<String, dynamic> json) =>
    TaskReadinessDto(
      taskId: json['taskId'] as String,
      readiness: json['readiness'] as String,
    );

Map<String, dynamic> _$TaskReadinessDtoToJson(TaskReadinessDto instance) =>
    <String, dynamic>{
      'taskId': instance.taskId,
      'readiness': instance.readiness,
    };
