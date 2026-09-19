import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/work_item.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/requests.dart';
import '../dto/work_item_dto.dart';
import '../mappers/mappers.dart';

class ApiWorkItemRepository implements WorkItemRepository {
  const ApiWorkItemRepository(this._client);

  final ApiClient _client;

  @override
  Future<List<WorkItem>> listByProject(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJsonList(
      ApiEndpoints.projectTasks(projectId),
      cancelToken: cancelTokenOf(cancellation),
    );
    return json
        .map((e) => mapWorkItem(WorkItemDto.fromJson(e)))
        .toList(growable: false);
  }

  @override
  Future<WorkItem> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.task(id),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapWorkItem(WorkItemDto.fromJson(json));
  }

  @override
  Future<TaskReadiness> getReadiness(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.taskReadiness(id),
      cancelToken: cancelTokenOf(cancellation),
    );
    return TaskReadiness.fromWire(TaskReadinessDto.fromJson(json).readiness);
  }

  @override
  Future<WorkItem> updateStatus(
    String id,
    WorkItemStatus status, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.patchJson(
      ApiEndpoints.taskStatus(id),
      body: UpdateStatusRequest.workItemStatus(status).toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapWorkItem(WorkItemDto.fromJson(json));
  }
}
