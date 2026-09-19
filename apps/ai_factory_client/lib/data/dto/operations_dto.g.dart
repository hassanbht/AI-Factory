// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'operations_dto.dart';

// **************************************************************************
// JsonSerializableGenerator
// **************************************************************************

DeploymentInfoDto _$DeploymentInfoDtoFromJson(Map<String, dynamic> json) =>
    DeploymentInfoDto(
      applicationName: json['applicationName'] as String,
      environment: json['environment'] as String,
      version: json['version'] as String,
      informationalVersion: json['informationalVersion'] as String,
      frameworkDescription: json['frameworkDescription'] as String,
      osDescription: json['osDescription'] as String,
      processArchitecture: json['processArchitecture'] as String,
      isContainerized: json['isContainerized'] as bool,
      startedAtUtc: const UtcDateTimeConverter().fromJson(
        json['startedAtUtc'] as String,
      ),
      databaseProvider: json['databaseProvider'] as String,
      uptime: const TimeSpanConverter().fromJson(json['uptime'] as String?),
      gitCommit: json['gitCommit'] as String?,
      buildTimestamp: json['buildTimestamp'] as String?,
      gitBranch: json['gitBranch'] as String?,
      buildNumber: json['buildNumber'] as String?,
    );

Map<String, dynamic> _$DeploymentInfoDtoToJson(
  DeploymentInfoDto instance,
) => <String, dynamic>{
  'applicationName': instance.applicationName,
  'environment': instance.environment,
  'version': instance.version,
  'informationalVersion': instance.informationalVersion,
  'frameworkDescription': instance.frameworkDescription,
  'osDescription': instance.osDescription,
  'processArchitecture': instance.processArchitecture,
  'isContainerized': instance.isContainerized,
  'startedAtUtc': const UtcDateTimeConverter().toJson(instance.startedAtUtc),
  'uptime': const TimeSpanConverter().toJson(instance.uptime),
  'databaseProvider': instance.databaseProvider,
  'gitCommit': instance.gitCommit,
  'buildTimestamp': instance.buildTimestamp,
  'gitBranch': instance.gitBranch,
  'buildNumber': instance.buildNumber,
};

DatabaseInfoDto _$DatabaseInfoDtoFromJson(
  Map<String, dynamic> json,
) => DatabaseInfoDto(
  generatedAtUtc: const UtcDateTimeConverter().fromJson(
    json['generatedAtUtc'] as String,
  ),
  providerName: json['providerName'] as String,
  isReady: json['isReady'] as bool,
  commandTimeoutSeconds: (json['commandTimeoutSeconds'] as num).toInt(),
  retriesEnabled: json['retriesEnabled'] as bool,
  maxRetryAttempts: (json['maxRetryAttempts'] as num).toInt(),
  optimisticConcurrencyEnabled: json['optimisticConcurrencyEnabled'] as bool,
  operationsCount: (json['operationsCount'] as num).toInt(),
  failuresCount: (json['failuresCount'] as num).toInt(),
  retriesCount: (json['retriesCount'] as num).toInt(),
  transientFailuresCount: (json['transientFailuresCount'] as num).toInt(),
  concurrencyConflictsCount: (json['concurrencyConflictsCount'] as num).toInt(),
  busyFailuresCount: (json['busyFailuresCount'] as num).toInt(),
  lockedFailuresCount: (json['lockedFailuresCount'] as num).toInt(),
  transactionFailuresCount: (json['transactionFailuresCount'] as num).toInt(),
  averageDurationMs: (json['averageDurationMs'] as num).toDouble(),
);

Map<String, dynamic> _$DatabaseInfoDtoToJson(DatabaseInfoDto instance) =>
    <String, dynamic>{
      'generatedAtUtc': const UtcDateTimeConverter().toJson(
        instance.generatedAtUtc,
      ),
      'providerName': instance.providerName,
      'isReady': instance.isReady,
      'commandTimeoutSeconds': instance.commandTimeoutSeconds,
      'retriesEnabled': instance.retriesEnabled,
      'maxRetryAttempts': instance.maxRetryAttempts,
      'optimisticConcurrencyEnabled': instance.optimisticConcurrencyEnabled,
      'operationsCount': instance.operationsCount,
      'failuresCount': instance.failuresCount,
      'retriesCount': instance.retriesCount,
      'transientFailuresCount': instance.transientFailuresCount,
      'concurrencyConflictsCount': instance.concurrencyConflictsCount,
      'busyFailuresCount': instance.busyFailuresCount,
      'lockedFailuresCount': instance.lockedFailuresCount,
      'transactionFailuresCount': instance.transactionFailuresCount,
      'averageDurationMs': instance.averageDurationMs,
    };

ConfigurationInfoDto _$ConfigurationInfoDtoFromJson(
  Map<String, dynamic> json,
) => ConfigurationInfoDto(
  generatedAtUtc: const UtcDateTimeConverter().fromJson(
    json['generatedAtUtc'] as String,
  ),
  environmentName: json['environmentName'] as String,
  isValid: json['isValid'] as bool,
  database: ConfigurationDatabaseDto.fromJson(
    json['database'] as Map<String, dynamic>,
  ),
  secretManagement: SecretManagementInfoDto.fromJson(
    json['secretManagement'] as Map<String, dynamic>,
  ),
  security: SecurityInfoDto.fromJson(json['security'] as Map<String, dynamic>),
  resilience: ResilienceInfoDto.fromJson(
    json['resilience'] as Map<String, dynamic>,
  ),
  externalProviders:
      (json['externalProviders'] as List<dynamic>?)
          ?.map((e) => ProviderInfoDto.fromJson(e as Map<String, dynamic>))
          .toList() ??
      [],
  configurationSources:
      (json['configurationSources'] as List<dynamic>?)
          ?.map((e) => e as String)
          .toList() ??
      [],
);

Map<String, dynamic> _$ConfigurationInfoDtoToJson(
  ConfigurationInfoDto instance,
) => <String, dynamic>{
  'generatedAtUtc': const UtcDateTimeConverter().toJson(
    instance.generatedAtUtc,
  ),
  'environmentName': instance.environmentName,
  'isValid': instance.isValid,
  'database': instance.database,
  'secretManagement': instance.secretManagement,
  'security': instance.security,
  'resilience': instance.resilience,
  'externalProviders': instance.externalProviders,
  'configurationSources': instance.configurationSources,
};

ConfigurationDatabaseDto _$ConfigurationDatabaseDtoFromJson(
  Map<String, dynamic> json,
) => ConfigurationDatabaseDto(
  configured: json['configured'] as bool,
  provider: json['provider'] as String,
  hasConnectionString: json['hasConnectionString'] as bool,
);

Map<String, dynamic> _$ConfigurationDatabaseDtoToJson(
  ConfigurationDatabaseDto instance,
) => <String, dynamic>{
  'configured': instance.configured,
  'provider': instance.provider,
  'hasConnectionString': instance.hasConnectionString,
};

SecretManagementInfoDto _$SecretManagementInfoDtoFromJson(
  Map<String, dynamic> json,
) => SecretManagementInfoDto(
  enabled: json['enabled'] as bool,
  providerName: json['providerName'] as String,
  failFastOnMissingSecret: json['failFastOnMissingSecret'] as bool,
);

Map<String, dynamic> _$SecretManagementInfoDtoToJson(
  SecretManagementInfoDto instance,
) => <String, dynamic>{
  'enabled': instance.enabled,
  'providerName': instance.providerName,
  'failFastOnMissingSecret': instance.failFastOnMissingSecret,
};

SecurityInfoDto _$SecurityInfoDtoFromJson(Map<String, dynamic> json) =>
    SecurityInfoDto(
      operationalEndpointsSecured: json['operationalEndpointsSecured'] as bool,
      operationalKeyHeader: json['operationalKeyHeader'] as String,
      rateLimitingEnabled: json['rateLimitingEnabled'] as bool,
      maxRequestBodySizeBytes: (json['maxRequestBodySizeBytes'] as num).toInt(),
      corsEnabled: json['corsEnabled'] as bool,
      httpsRedirectEnabled: json['httpsRedirectEnabled'] as bool,
      hstsEnabled: json['hstsEnabled'] as bool,
    );

Map<String, dynamic> _$SecurityInfoDtoToJson(SecurityInfoDto instance) =>
    <String, dynamic>{
      'operationalEndpointsSecured': instance.operationalEndpointsSecured,
      'operationalKeyHeader': instance.operationalKeyHeader,
      'rateLimitingEnabled': instance.rateLimitingEnabled,
      'maxRequestBodySizeBytes': instance.maxRequestBodySizeBytes,
      'corsEnabled': instance.corsEnabled,
      'httpsRedirectEnabled': instance.httpsRedirectEnabled,
      'hstsEnabled': instance.hstsEnabled,
    };

ResilienceInfoDto _$ResilienceInfoDtoFromJson(Map<String, dynamic> json) =>
    ResilienceInfoDto(
      configuredProfilesCount: (json['configuredProfilesCount'] as num).toInt(),
      profileNames:
          (json['profileNames'] as List<dynamic>?)
              ?.map((e) => e as String)
              .toList() ??
          [],
    );

Map<String, dynamic> _$ResilienceInfoDtoToJson(ResilienceInfoDto instance) =>
    <String, dynamic>{
      'configuredProfilesCount': instance.configuredProfilesCount,
      'profileNames': instance.profileNames,
    };

ProviderInfoDto _$ProviderInfoDtoFromJson(Map<String, dynamic> json) =>
    ProviderInfoDto(
      name: json['name'] as String,
      enabled: json['enabled'] as bool,
      hasSecretReference: json['hasSecretReference'] as bool,
      secretReferenceName: json['secretReferenceName'] as String?,
    );

Map<String, dynamic> _$ProviderInfoDtoToJson(ProviderInfoDto instance) =>
    <String, dynamic>{
      'name': instance.name,
      'enabled': instance.enabled,
      'hasSecretReference': instance.hasSecretReference,
      'secretReferenceName': instance.secretReferenceName,
    };

SchedulerStatusDto _$SchedulerStatusDtoFromJson(Map<String, dynamic> json) =>
    SchedulerStatusDto(
      enabled: json['enabled'] as bool,
      maxConcurrentTasks: (json['maxConcurrentTasks'] as num).toInt(),
      lastCycleDispatchedCount: (json['lastCycleDispatchedCount'] as num)
          .toInt(),
      lastCycleFailureCount: (json['lastCycleFailureCount'] as num).toInt(),
      lastCycleSkippedDueToConcurrency:
          (json['lastCycleSkippedDueToConcurrency'] as num).toInt(),
      totalDispatched: (json['totalDispatched'] as num).toInt(),
      totalFailures: (json['totalFailures'] as num).toInt(),
      lastCycleAtUtc: const NullableUtcDateTimeConverter().fromJson(
        json['lastCycleAtUtc'] as String?,
      ),
      lastCycleProjectId: json['lastCycleProjectId'] as String?,
    );

Map<String, dynamic> _$SchedulerStatusDtoToJson(
  SchedulerStatusDto instance,
) => <String, dynamic>{
  'enabled': instance.enabled,
  'maxConcurrentTasks': instance.maxConcurrentTasks,
  'lastCycleAtUtc': const NullableUtcDateTimeConverter().toJson(
    instance.lastCycleAtUtc,
  ),
  'lastCycleProjectId': instance.lastCycleProjectId,
  'lastCycleDispatchedCount': instance.lastCycleDispatchedCount,
  'lastCycleFailureCount': instance.lastCycleFailureCount,
  'lastCycleSkippedDueToConcurrency': instance.lastCycleSkippedDueToConcurrency,
  'totalDispatched': instance.totalDispatched,
  'totalFailures': instance.totalFailures,
};
