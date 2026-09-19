import 'package:json_annotation/json_annotation.dart';

part 'problem_details_dto.g.dart';

/// RFC 7807 payload produced by `GlobalExceptionMiddleware` /
/// `ApiExceptionMapper`, including the AI Factory extensions
/// (`code`, `correlationId`, `traceId`, `message`).
///
/// This is the reference DTO for the serialization convention established in
/// Phase 19.1: DTOs live in `data/dto`, are generated with
/// `json_serializable`, know nothing about the UI, and are converted to a
/// domain type by a mapper before leaving the data layer.
@JsonSerializable(createToJson: false, fieldRename: FieldRename.none)
class ProblemDetailsDto {
  const ProblemDetailsDto({
    this.type,
    this.title,
    this.status,
    this.detail,
    this.instance,
    this.code,
    this.correlationId,
    this.traceId,
    this.message,
  });

  factory ProblemDetailsDto.fromJson(Map<String, dynamic> json) =>
      _$ProblemDetailsDtoFromJson(json);

  final String? type;
  final String? title;
  final int? status;
  final String? detail;
  final String? instance;

  /// Stable machine-readable discriminator: `conflict`, `not_found`,
  /// `validation_error`, `too_many_requests`, ...
  final String? code;

  final String? correlationId;
  final String? traceId;
  final String? message;

  /// The server sends both; `detail` is the sanitized text, `message` mirrors
  /// it. Prefer `detail`.
  String? get bestDetail =>
      (detail != null && detail!.isNotEmpty) ? detail : message;
}
