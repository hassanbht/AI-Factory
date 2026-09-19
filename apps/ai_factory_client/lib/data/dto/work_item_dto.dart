import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'work_item_dto.g.dart';

/// `ToWorkItemDto` in `apps/src/AI.Factory.Api/Program.cs`.
///
/// `maxAttempts`, `acceptanceCriteria`, `planKey`, `dependsOn`,
/// `startedAtUtc` and `completedAtUtc` exist on the backend entity but are not
/// part of the response contract — see gaps `G-DEPS` and `G-MAXATTEMPTS`.
/// They are absent here rather than fabricated.
@JsonSerializable()
class WorkItemDto {
  const WorkItemDto({
    required this.id,
    required this.projectId,
    required this.type,
    required this.title,
    required this.status,
    required this.priority,
    required this.attemptCount,
    required this.repairAttemptCount,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
    this.currentError,
    this.lastHeartbeatAtUtc,
  });

  factory WorkItemDto.fromJson(Map<String, dynamic> json) =>
      _$WorkItemDtoFromJson(json);

  final String id;
  final String projectId;

  /// Wire value of `WorkItemType`, e.g. `Implementation`.
  final String type;

  final String title;
  final String? description;

  /// Persisted as a free-form string on the backend, so it may hold a value
  /// outside `WorkItemStatus`.
  final String status;

  /// `Critical` | `High` | `Normal` | `Low`.
  final String priority;

  final int attemptCount;
  final int repairAttemptCount;
  final String? currentError;

  @NullableUtcDateTimeConverter()
  final DateTime? lastHeartbeatAtUtc;

  @UtcDateTimeConverter()
  final DateTime createdAtUtc;

  @UtcDateTimeConverter()
  final DateTime updatedAtUtc;

  Map<String, dynamic> toJson() => _$WorkItemDtoToJson(this);
}

/// `GET /api/tasks/{id}/readiness` -> `{ taskId, readiness }`.
@JsonSerializable()
class TaskReadinessDto {
  const TaskReadinessDto({required this.taskId, required this.readiness});

  factory TaskReadinessDto.fromJson(Map<String, dynamic> json) =>
      _$TaskReadinessDtoFromJson(json);

  final String taskId;

  /// Wire value of `TaskReadinessStatus`, e.g. `WaitingForDependency`.
  final String readiness;

  Map<String, dynamic> toJson() => _$TaskReadinessDtoToJson(this);
}
