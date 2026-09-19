import 'package:json_annotation/json_annotation.dart';

part 'project_plan_dto.g.dart';

/// `GET /api/projects/{projectId}/plan`.
///
/// The only endpoint that exposes dependency edges: `dependsOn` holds plan
/// keys and each task carries its `workItemId`, which is what lets the client
/// build the WorkItem graph (gap `G-DEPS`).
@JsonSerializable()
class ProjectPlanDto {
  const ProjectPlanDto({
    required this.projectId,
    required this.planHash,
    required this.tasks,
  });

  factory ProjectPlanDto.fromJson(Map<String, dynamic> json) =>
      _$ProjectPlanDtoFromJson(json);

  final String projectId;
  final String planHash;
  final List<ProjectPlanTaskDto> tasks;

  Map<String, dynamic> toJson() => _$ProjectPlanDtoToJson(this);
}

@JsonSerializable()
class ProjectPlanTaskDto {
  const ProjectPlanTaskDto({
    required this.planKey,
    required this.workItemId,
    required this.title,
    required this.type,
    required this.priority,
    required this.status,
    required this.readiness,
    this.dependsOn = const <String>[],
  });

  factory ProjectPlanTaskDto.fromJson(Map<String, dynamic> json) =>
      _$ProjectPlanTaskDtoFromJson(json);

  final String planKey;
  final String workItemId;
  final String title;

  /// Wire value of `WorkItemType`.
  final String type;

  final String priority;

  /// Plan keys, not work item ids.
  @JsonKey(defaultValue: <String>[])
  final List<String> dependsOn;

  /// Raw work item status string.
  final String status;

  /// Wire value of `TaskReadinessStatus`.
  final String readiness;

  Map<String, dynamic> toJson() => _$ProjectPlanTaskDtoToJson(this);
}
