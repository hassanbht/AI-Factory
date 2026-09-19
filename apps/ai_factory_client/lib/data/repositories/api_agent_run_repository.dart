import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/agent_dto.dart';
import '../dto/requests.dart';
import '../mappers/mappers.dart';

class ApiAgentRunRepository implements AgentRunRepository {
  const ApiAgentRunRepository(this._client);

  final ApiClient _client;

  @override
  Future<PagedResult<AgentRun>> list(
    AgentRunQuery query, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.agentRuns,
      queryParameters: query.toQueryParameters(),
      cancelToken: cancelTokenOf(cancellation),
    );

    final page = AgentRunPageDto.fromJson(json);
    return PagedResult<AgentRun>(
      items: page.items.map(mapAgentRun).toList(growable: false),
      totalCount: page.totalCount,
      page: page.page,
      pageSize: page.pageSize,
    );
  }

  @override
  Future<AgentRun> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.agentRun(id),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapAgentRun(AgentRunDto.fromJson(json));
  }

  @override
  Future<AgentRun> create(
    CreateAgentRunRequest request, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.postJson(
      ApiEndpoints.agentRuns,
      body: request.toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapAgentRun(AgentRunDto.fromJson(json));
  }

  /// The endpoint answers `202 Accepted` with no body: cancellation is
  /// requested, not completed. Callers re-read the run instead of assuming
  /// it is already `Cancelled`.
  @override
  Future<void> cancelRun(String id, {RequestCancellation? cancellation}) async {
    await _client.post<dynamic>(
      ApiEndpoints.agentRunCancel(id),
      cancelToken: cancelTokenOf(cancellation),
    );
  }

  @override
  Future<List<AgentDefinition>> listDefinitions({
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJsonList(
      ApiEndpoints.agentDefinitions,
      cancelToken: cancelTokenOf(cancellation),
    );
    return json
        .map((e) => mapAgentDefinition(AgentDefinitionDto.fromJson(e)))
        .toList(growable: false);
  }
}
