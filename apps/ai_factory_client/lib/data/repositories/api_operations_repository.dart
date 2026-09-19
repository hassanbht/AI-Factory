import '../../core/errors/app_error.dart';
import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/agent_dto.dart';
import '../dto/diagnostics_dto.dart';
import '../dto/operations_dto.dart';
import '../mappers/mappers.dart';

class ApiDiagnosticsRepository implements DiagnosticsRepository {
  const ApiDiagnosticsRepository(this._client);

  final ApiClient _client;

  @override
  Future<ProjectDiagnostics> getProjectDiagnostics(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.projectDiagnostics(projectId),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProjectDiagnostics(ProjectDiagnosticsDto.fromJson(json));
  }

  @override
  Future<AgentMetrics> getAgentMetrics({
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.agentMetrics,
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapAgentMetrics(AgentMetricsDto.fromJson(json));
  }
}

class ApiOperationsRepository implements OperationsRepository {
  const ApiOperationsRepository(this._client);

  final ApiClient _client;

  /// `/health/ready` answers `503` with the body `Unhealthy` when a
  /// dependency is down. That is an answer, not a transport failure, so it
  /// becomes `isReady: false` rather than a thrown error — the caller asked
  /// "is it healthy", and "no" is a valid reply.
  ///
  /// A `/health/live` that cannot be reached at all is genuinely unknown, so
  /// it reports [SystemHealth.unreachable] instead of guessing.
  @override
  Future<SystemHealth> getHealth({RequestCancellation? cancellation}) async {
    final token = cancelTokenOf(cancellation);
    final checkedAt = DateTime.now().toUtc();

    bool isAlive;
    try {
      final live = await _client.getText(
        ApiEndpoints.healthLive,
        cancelToken: token,
      );
      isAlive = _isHealthy(live);
    } on RequestCancelledError {
      rethrow;
    } on AppError {
      return SystemHealth.unreachable(checkedAt);
    }

    bool isReady;
    try {
      final ready = await _client.getText(
        ApiEndpoints.healthReady,
        cancelToken: token,
      );
      isReady = _isHealthy(ready);
    } on RequestCancelledError {
      rethrow;
    } on ProviderFailure {
      // 503 with an "Unhealthy" body: reachable, but not ready.
      isReady = false;
    } on ServerError {
      isReady = false;
    }

    return SystemHealth(
      isAlive: isAlive,
      isReady: isReady,
      checkedAtUtc: checkedAt,
    );
  }

  static bool _isHealthy(String body) => body.trim().toLowerCase() == 'healthy';

  @override
  Future<DeploymentInfo> getDeployment({
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.deployment,
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapDeploymentInfo(DeploymentInfoDto.fromJson(json));
  }

  @override
  Future<DatabaseInfo> getDatabase({RequestCancellation? cancellation}) async {
    final json = await _client.getJson(
      ApiEndpoints.database,
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapDatabaseInfo(DatabaseInfoDto.fromJson(json));
  }

  @override
  Future<ConfigurationInfo> getConfiguration({
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.configuration,
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapConfigurationInfo(ConfigurationInfoDto.fromJson(json));
  }

  @override
  Future<SchedulerStatus> getSchedulerStatus({
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.schedulerStatus,
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapSchedulerStatus(SchedulerStatusDto.fromJson(json));
  }
}
