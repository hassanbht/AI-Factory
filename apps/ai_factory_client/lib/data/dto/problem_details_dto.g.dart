// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'problem_details_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

ProblemDetailsDto _$ProblemDetailsDtoFromJson(Map<String, dynamic> json) =>
    ProblemDetailsDto(
      type: json['type'] as String?,
      title: json['title'] as String?,
      status: (json['status'] as num?)?.toInt(),
      detail: json['detail'] as String?,
      instance: json['instance'] as String?,
      code: json['code'] as String?,
      correlationId: json['correlationId'] as String?,
      traceId: json['traceId'] as String?,
      message: json['message'] as String?,
    );
