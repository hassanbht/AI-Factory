import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'agent_dto.g.dart';

/// `ToAgentDefinitionDto`. `systemPrompt` is never returned by the API and is
/// therefore not modelled.
@JsonSerializable()
class AgentDefinitionDto {
  const AgentDefinitionDto({
    required this.id,
    required this.name,
    required this.agentType,
    required this.enabled,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
  });

  factory AgentDefinitionDto.fromJson(Map<String, dynamic> json) =>
      _$AgentDefinitionDtoFromJson(json);

  final String id;
  final String name;
  final String? description;

  /// Matches a `WorkItemType` wire value for dispatchable agents.
  final String agentType;

  final bool enabled;

  @UtcDateTimeConverter()
  final DateTime createdAtUtc;

  @UtcDateTimeConverter()
  final DateTime updatedAtUtc;

  Map<String, dynamic> toJson() => _$AgentDefinitionDtoToJson(this);
}

/// `ToAgentRunDto`.
///
/// `provider`, `model` and the token counters are persisted on the backend
/// entity but are **not** in the response contract (gap `G-RUN-TELEMETRY`),
/// and no fallback information exists at all (gap `G-FALLBACK`). They are
/// absent here rather than invented; the UI states that the API does not
/// expose them.
@JsonSerializable()
class AgentRunDto {
  const AgentRunDto({
    required this.id,
    required this.agentDefinitionId,
    required this.status,
    required this.attempt,
    required this.createdAtUtc,
    required this.correlationId,
    this.projectId,
    this.workItemId,
    this.startedAtUtc,
    this.completedAtUtc,
    this.durationMs,
    this.output,
    this.failureCategory,
    this.errorMessage,
    this.structuredResult,
  });

  factory AgentRunDto.fromJson(Map<String, dynamic> json) =>
      _$AgentRunDtoFromJson(json);

  final String id;
  final String? projectId;
  final String? workItemId;
  final String agentDefinitionId;

  /// Wire value of `AgentRunStatus`, e.g. `TimedOut`.
  final String status;

  final int attempt;

  @UtcDateTimeConverter()
  final DateTime createdAtUtc;

  @NullableUtcDateTimeConverter()
  final DateTime? startedAtUtc;

  @NullableUtcDateTimeConverter()
  final DateTime? completedAtUtc;

  /// Server-computed; null until the run both started and completed.
  final int? durationMs;

  final String? output;

  /// Wire value of `FailureCategory`; explicitly stringified by the API.
  final String? failureCategory;

  final String? errorMessage;
  final String correlationId;

  /// Null when absent or when the persisted JSON could not be parsed — the
  /// API degrades to null rather than surfacing a deserialization error.
  final StructuredAgentResultDto? structuredResult;

  Map<String, dynamic> toJson() => _$AgentRunDtoToJson(this);
}

/// The `structuredResult` object nested in an agent run.
@JsonSerializable()
class StructuredAgentResultDto {
  const StructuredAgentResultDto({
    required this.version,
    required this.status,
    this.summary,
    this.output,
    this.data,
    this.warnings = const <String>[],
    this.errors = const <String>[],
    this.artifacts = const <String>[],
    this.metadata,
  });

  factory StructuredAgentResultDto.fromJson(Map<String, dynamic> json) =>
      _$StructuredAgentResultDtoFromJson(json);

  final int version;

  /// `Succeeded` | `Failed` | `Cancelled` | `TimedOut` | `Invalid`.
  final String status;

  final String? summary;
  final String? output;

  /// Free-form JSON the agent produced, carried as an opaque string.
  final String? data;

  @JsonKey(defaultValue: <String>[])
  final List<String> warnings;

  @JsonKey(defaultValue: <String>[])
  final List<String> errors;

  /// Artifact names only — there is no artifact API (gap `G-ARTIFACTS`).
  @JsonKey(defaultValue: <String>[])
  final List<String> artifacts;

  final Map<String, String>? metadata;

  Map<String, dynamic> toJson() => _$StructuredAgentResultDtoToJson(this);
}

/// `GET /api/agents/runs` — the only paged list contract in the API.
@JsonSerializable()
class AgentRunPageDto {
  const AgentRunPageDto({
    required this.items,
    required this.totalCount,
    required this.page,
    required this.pageSize,
  });

  factory AgentRunPageDto.fromJson(Map<String, dynamic> json) =>
      _$AgentRunPageDtoFromJson(json);

  final List<AgentRunDto> items;
  final int totalCount;
  final int page;
  final int pageSize;

  Map<String, dynamic> toJson() => _$AgentRunPageDtoToJson(this);
}

/// `GET /api/agents/metrics` (operational key required).
///
/// Process-lifetime counters that reset when the API restarts, and are global
/// rather than per project. There is no stale-run counter here
/// (gap `G-STALE-GLOBAL`).
@JsonSerializable()
class AgentMetricsDto {
  const AgentMetricsDto({
    required this.queued,
    required this.running,
    required this.created,
    required this.started,
    required this.succeeded,
    required this.failed,
    required this.timedOut,
    required this.cancelled,
    required this.workerClaimSuccess,
    this.averageDurationMs,
    this.totalDurationMs,
  });

  factory AgentMetricsDto.fromJson(Map<String, dynamic> json) =>
      _$AgentMetricsDtoFromJson(json);

  final int queued;
  final int running;
  final int created;
  final int started;
  final int succeeded;
  final int failed;
  final int timedOut;
  final int cancelled;
  final int workerClaimSuccess;
  final double? averageDurationMs;
  final int? totalDurationMs;

  Map<String, dynamic> toJson() => _$AgentMetricsDtoToJson(this);
}
