import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'project_dto.g.dart';

/// `ToProjectDto` in `apps/src/AI.Factory.Api/Program.cs`.
///
/// Exactly the fields the API returns — `rootPath` is deliberately not part of
/// the contract, so it is deliberately absent here too.
@JsonSerializable()
class ProjectDto {
  const ProjectDto({
    required this.id,
    required this.name,
    required this.slug,
    required this.status,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
    this.gitRepository,
    this.gitBranch,
    this.lifecycleStartedAtUtc,
    this.currentError,
  });

  factory ProjectDto.fromJson(Map<String, dynamic> json) =>
      _$ProjectDtoFromJson(json);

  final String id;
  final String name;
  final String slug;
  final String? description;

  /// Wire value of `ProjectStatus`, e.g. `HumanReview`.
  final String status;

  final String? gitRepository;
  final String? gitBranch;

  /// Non-null once the project has been started; null means operator-driven.
  @NullableUtcDateTimeConverter()
  final DateTime? lifecycleStartedAtUtc;

  final String? currentError;

  @UtcDateTimeConverter()
  final DateTime createdAtUtc;

  @UtcDateTimeConverter()
  final DateTime updatedAtUtc;

  Map<String, dynamic> toJson() => _$ProjectDtoToJson(this);
}
