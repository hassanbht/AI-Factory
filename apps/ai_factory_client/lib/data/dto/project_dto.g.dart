// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'project_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

ProjectDto _$ProjectDtoFromJson(Map<String, dynamic> json) => ProjectDto(
  id: json['id'] as String,
  name: json['name'] as String,
  slug: json['slug'] as String,
  status: json['status'] as String,
  createdAtUtc: const UtcDateTimeConverter().fromJson(
    json['createdAtUtc'] as String,
  ),
  updatedAtUtc: const UtcDateTimeConverter().fromJson(
    json['updatedAtUtc'] as String,
  ),
  description: json['description'] as String?,
  gitRepository: json['gitRepository'] as String?,
  gitBranch: json['gitBranch'] as String?,
  lifecycleStartedAtUtc: const NullableUtcDateTimeConverter().fromJson(
    json['lifecycleStartedAtUtc'] as String?,
  ),
  currentError: json['currentError'] as String?,
);

Map<String, dynamic> _$ProjectDtoToJson(
  ProjectDto instance,
) => <String, dynamic>{
  'id': instance.id,
  'name': instance.name,
  'slug': instance.slug,
  'description': instance.description,
  'status': instance.status,
  'gitRepository': instance.gitRepository,
  'gitBranch': instance.gitBranch,
  'lifecycleStartedAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.lifecycleStartedAtUtc,
  ),
  'currentError': instance.currentError,
  'createdAtUtc': const UtcDateTimeConverter().toJson(instance.createdAtUtc),
  'updatedAtUtc': const UtcDateTimeConverter().toJson(instance.updatedAtUtc),
};
