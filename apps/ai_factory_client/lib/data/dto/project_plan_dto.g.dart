// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'project_plan_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

ProjectPlanDto _$ProjectPlanDtoFromJson(Map<String, dynamic> json) =>
    ProjectPlanDto(
      projectId: json['projectId'] as String,
      planHash: json['planHash'] as String,
      tasks: (json['tasks'] as List<dynamic>)
          .map((e) => ProjectPlanTaskDto.fromJson(e as Map<String, dynamic>))
          .toList(),
    );

Map<String, dynamic> _$ProjectPlanDtoToJson(ProjectPlanDto instance) =>
    <String, dynamic>{
      'projectId': instance.projectId,
      'planHash': instance.planHash,
      'tasks': instance.tasks,
    };

ProjectPlanTaskDto _$ProjectPlanTaskDtoFromJson(Map<String, dynamic> json) =>
    ProjectPlanTaskDto(
      planKey: json['planKey'] as String,
      workItemId: json['workItemId'] as String,
      title: json['title'] as String,
      type: json['type'] as String,
      priority: json['priority'] as String,
      status: json['status'] as String,
      readiness: json['readiness'] as String,
      dependsOn:
          (json['dependsOn'] as List<dynamic>?)
              ?.map((e) => e as String)
              .toList() ??
          [],
    );

Map<String, dynamic> _$ProjectPlanTaskDtoToJson(ProjectPlanTaskDto instance) =>
    <String, dynamic>{
      'planKey': instance.planKey,
      'workItemId': instance.workItemId,
      'title': instance.title,
      'type': instance.type,
      'priority': instance.priority,
      'dependsOn': instance.dependsOn,
      'status': instance.status,
      'readiness': instance.readiness,
    };
