import 'package:json_annotation/json_annotation.dart';

import 'json_converters.dart';

part 'operations_dto.g.dart';

/// `GET /api/operations/deployment` (operational key required).
@JsonSerializable()
class DeploymentInfoDto {
  const DeploymentInfoDto({
    required this.applicationName,
    required this.environment,
    required this.version,
    required this.informationalVersion,
    required this.frameworkDescription,
    required this.osDescription,
    required this.processArchitecture,
    required this.isContainerized,
    required this.startedAtUtc,
    required this.databaseProvider,
    this.uptime,
    this.gitCommit,
    this.buildTimestamp,
    this.gitBranch,
    this.buildNumber,
  });

  factory DeploymentInfoDto.fromJson(Map<String, dynamic> json) =>
      _$DeploymentInfoDtoFromJson(json);

  final String applicationName;
  final String environment;
  final String version;
  final String informationalVersion;
  final String frameworkDescription;
  final String osDescription;
  final String processArchitecture;
  final bool isContainerized;

  @UtcDateTimeConverter()
  final DateTime startedAtUtc;

  /// .NET `TimeSpan`, e.g. `"01:12:33.4560000"`.
  @TimeSpanConverter()
  final Duration? uptime;

  final String databaseProvider;
  final String? gitCommit;
  final String? buildTimestamp;
  final String? gitBranch;
  final String? buildNumber;

  Map<String, dynamic> toJson() => _$DeploymentInfoDtoToJson(this);
}

/// `GET /api/operations/database` (operational key required).
/// Contains counters and settings only — never a connection string.
@JsonSerializable()
class DatabaseInfoDto {
  const DatabaseInfoDto({
    required this.generatedAtUtc,
    required this.providerName,
    required this.isReady,
    required this.commandTimeoutSeconds,
    required this.retriesEnabled,
    required this.maxRetryAttempts,
    required this.optimisticConcurrencyEnabled,
    required this.operationsCount,
    required this.failuresCount,
    required this.retriesCount,
    required this.transientFailuresCount,
    required this.concurrencyConflictsCount,
    required this.busyFailuresCount,
    required this.lockedFailuresCount,
    required this.transactionFailuresCount,
    required this.averageDurationMs,
  });

  factory DatabaseInfoDto.fromJson(Map<String, dynamic> json) =>
      _$DatabaseInfoDtoFromJson(json);

  @UtcDateTimeConverter()
  final DateTime generatedAtUtc;

  final String providerName;
  final bool isReady;
  final int commandTimeoutSeconds;
  final bool retriesEnabled;
  final int maxRetryAttempts;
  final bool optimisticConcurrencyEnabled;
  final int operationsCount;
  final int failuresCount;
  final int retriesCount;
  final int transientFailuresCount;
  final int concurrencyConflictsCount;
  final int busyFailuresCount;
  final int lockedFailuresCount;
  final int transactionFailuresCount;
  final double averageDurationMs;

  Map<String, dynamic> toJson() => _$DatabaseInfoDtoToJson(this);
}

/// `GET /api/operations/configuration` (operational key required).
///
/// Booleans, names and counts only. The backend guarantees no secret value
/// appears in this snapshot, and nothing here can carry one.
@JsonSerializable()
class ConfigurationInfoDto {
  const ConfigurationInfoDto({
    required this.generatedAtUtc,
    required this.environmentName,
    required this.isValid,
    required this.database,
    required this.secretManagement,
    required this.security,
    required this.resilience,
    this.externalProviders = const <ProviderInfoDto>[],
    this.configurationSources = const <String>[],
  });

  factory ConfigurationInfoDto.fromJson(Map<String, dynamic> json) =>
      _$ConfigurationInfoDtoFromJson(json);

  @UtcDateTimeConverter()
  final DateTime generatedAtUtc;

  final String environmentName;
  final bool isValid;
  final ConfigurationDatabaseDto database;
  final SecretManagementInfoDto secretManagement;
  final SecurityInfoDto security;
  final ResilienceInfoDto resilience;

  @JsonKey(defaultValue: <ProviderInfoDto>[])
  final List<ProviderInfoDto> externalProviders;

  @JsonKey(defaultValue: <String>[])
  final List<String> configurationSources;

  Map<String, dynamic> toJson() => _$ConfigurationInfoDtoToJson(this);
}

@JsonSerializable()
class ConfigurationDatabaseDto {
  const ConfigurationDatabaseDto({
    required this.configured,
    required this.provider,
    required this.hasConnectionString,
  });

  factory ConfigurationDatabaseDto.fromJson(Map<String, dynamic> json) =>
      _$ConfigurationDatabaseDtoFromJson(json);

  final bool configured;
  final String provider;

  /// Whether one is configured — never the value itself.
  final bool hasConnectionString;

  Map<String, dynamic> toJson() => _$ConfigurationDatabaseDtoToJson(this);
}

@JsonSerializable()
class SecretManagementInfoDto {
  const SecretManagementInfoDto({
    required this.enabled,
    required this.providerName,
    required this.failFastOnMissingSecret,
  });

  factory SecretManagementInfoDto.fromJson(Map<String, dynamic> json) =>
      _$SecretManagementInfoDtoFromJson(json);

  final bool enabled;
  final String providerName;
  final bool failFastOnMissingSecret;

  Map<String, dynamic> toJson() => _$SecretManagementInfoDtoToJson(this);
}

@JsonSerializable()
class SecurityInfoDto {
  const SecurityInfoDto({
    required this.operationalEndpointsSecured,
    required this.operationalKeyHeader,
    required this.rateLimitingEnabled,
    required this.maxRequestBodySizeBytes,
    required this.corsEnabled,
    required this.httpsRedirectEnabled,
    required this.hstsEnabled,
  });

  factory SecurityInfoDto.fromJson(Map<String, dynamic> json) =>
      _$SecurityInfoDtoFromJson(json);

  /// Whether the operational endpoints require a key at all.
  final bool operationalEndpointsSecured;

  /// The header *name* the backend expects, never a key value.
  final String operationalKeyHeader;

  final bool rateLimitingEnabled;
  final int maxRequestBodySizeBytes;
  final bool corsEnabled;
  final bool httpsRedirectEnabled;
  final bool hstsEnabled;

  Map<String, dynamic> toJson() => _$SecurityInfoDtoToJson(this);
}

@JsonSerializable()
class ResilienceInfoDto {
  const ResilienceInfoDto({
    required this.configuredProfilesCount,
    this.profileNames = const <String>[],
  });

  factory ResilienceInfoDto.fromJson(Map<String, dynamic> json) =>
      _$ResilienceInfoDtoFromJson(json);

  final int configuredProfilesCount;

  @JsonKey(defaultValue: <String>[])
  final List<String> profileNames;

  Map<String, dynamic> toJson() => _$ResilienceInfoDtoToJson(this);
}

@JsonSerializable()
class ProviderInfoDto {
  const ProviderInfoDto({
    required this.name,
    required this.enabled,
    required this.hasSecretReference,
    this.secretReferenceName,
  });

  factory ProviderInfoDto.fromJson(Map<String, dynamic> json) =>
      _$ProviderInfoDtoFromJson(json);

  final String name;
  final bool enabled;

  /// Whether a secret is referenced — the secret itself never leaves the API.
  final bool hasSecretReference;

  /// The *name* of the secret reference, e.g. `OPENROUTER_API_KEY`.
  final String? secretReferenceName;

  Map<String, dynamic> toJson() => _$ProviderInfoDtoToJson(this);
}

/// `GET /api/scheduler/status` — no operational key required.
///
/// In-memory counters that reset on restart. This is the closest available
/// proxy for worker liveness (gap `G-WORKER-HEALTH`).
@JsonSerializable()
class SchedulerStatusDto {
  const SchedulerStatusDto({
    required this.enabled,
    required this.maxConcurrentTasks,
    required this.lastCycleDispatchedCount,
    required this.lastCycleFailureCount,
    required this.lastCycleSkippedDueToConcurrency,
    required this.totalDispatched,
    required this.totalFailures,
    this.lastCycleAtUtc,
    this.lastCycleProjectId,
  });

  factory SchedulerStatusDto.fromJson(Map<String, dynamic> json) =>
      _$SchedulerStatusDtoFromJson(json);

  final bool enabled;
  final int maxConcurrentTasks;

  @NullableUtcDateTimeConverter()
  final DateTime? lastCycleAtUtc;

  final String? lastCycleProjectId;
  final int lastCycleDispatchedCount;
  final int lastCycleFailureCount;
  final int lastCycleSkippedDueToConcurrency;
  final int totalDispatched;
  final int totalFailures;

  Map<String, dynamic> toJson() => _$SchedulerStatusDtoToJson(this);
}
