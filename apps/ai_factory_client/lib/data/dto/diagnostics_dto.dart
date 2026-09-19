import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'diagnostics_dto.g.dart';

/// `GET /api/operations/projects/{projectId}/diagnostics` -> `ProjectDiagnosticSummary`
/// (operational key required).
///
/// **Numeric enums.** This response is a raw C# record, and the API configures
/// no `JsonStringEnumConverter`, so its enum members serialize as *integers*
/// rather than names — verified against the running `v1.0.0` API, where an
/// audit entry of the same shape returned `"eventType":31,"severity":1`.
/// `lastFailureCategory` and `latestApprovalDecision` are therefore typed as
/// `Object?` and resolved by the mapper, which accepts an ordinal today and a
/// name if the backend ever adds the converter.
@JsonSerializable()
class ProjectDiagnosticsDto {
  const ProjectDiagnosticsDto({
    required this.projectId,
    required this.projectStatus,
    required this.repairAttemptCount,
    required this.activeAgentRuns,
    required this.staleAgentRuns,
    required this.attemptCount,
    this.latestTaskId,
    this.latestTaskStatus,
    this.latestRunId,
    this.latestRunStatus,
    this.lastSuccessAtUtc,
    this.lastFailureAtUtc,
    this.lastFailureCategory,
    this.lastFailureMessage,
    this.latestApprovalDecision,
    this.workItemStatusCounts,
    this.executionDurationMs,
  });

  factory ProjectDiagnosticsDto.fromJson(Map<String, dynamic> json) =>
      _$ProjectDiagnosticsDtoFromJson(json);

  final String projectId;

  /// Already a string on the wire: the service stores it as `string`.
  final String projectStatus;

  final String? latestTaskId;
  final String? latestTaskStatus;
  final int repairAttemptCount;
  final String? latestRunId;
  final String? latestRunStatus;

  @NullableUtcDateTimeConverter()
  final DateTime? lastSuccessAtUtc;

  @NullableUtcDateTimeConverter()
  final DateTime? lastFailureAtUtc;

  /// Ordinal of `FailureCategory` (see the class comment).
  final Object? lastFailureCategory;

  final String? lastFailureMessage;

  /// Ordinal of `ApprovalDecision`.
  final Object? latestApprovalDecision;

  /// Raw work item status string -> count, e.g. `{"Ready": 2}`.
  final Map<String, int>? workItemStatusCounts;

  final int activeAgentRuns;

  /// Per project only; there is no global stale count (gap `G-STALE-GLOBAL`).
  final int staleAgentRuns;

  final int? executionDurationMs;
  final int attemptCount;

  Map<String, dynamic> toJson() => _$ProjectDiagnosticsDtoToJson(this);
}
