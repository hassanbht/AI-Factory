import '../../core/errors/app_error.dart';
import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../../domain/models/work_item.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/project_dto.dart';
import '../dto/project_plan_dto.dart';
import '../dto/requests.dart';
import '../dto/work_item_dto.dart';
import '../mappers/mappers.dart';

class ApiProjectRepository implements ProjectRepository {
  const ApiProjectRepository(this._client);

  final ApiClient _client;

  @override
  Future<List<Project>> list({RequestCancellation? cancellation}) async {
    final json = await _client.getJsonList(
      ApiEndpoints.projects,
      cancelToken: cancelTokenOf(cancellation),
    );
    return json
        .map((e) => mapProject(ProjectDto.fromJson(e)))
        .toList(growable: false);
  }

  @override
  Future<Project> getById(
    String id, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.project(id),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }

  @override
  Future<Project> create(
    CreateProjectRequest request, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.postJson(
      ApiEndpoints.projects,
      body: request.toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }

  @override
  Future<Project> initializeSource(
    String id,
    InitializeProjectSourceRequest request, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.postJson(
      ApiEndpoints.projectSource(id),
      body: request.toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }

  @override
  Future<Project> start(
    String id, {
    String? actor,
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.postJson(
      ApiEndpoints.projectStart(id),
      body: ActorRequest(actor: actor).toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }

  @override
  Future<Project> updateStatus(
    String id,
    ProjectStatus status, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.patchJson(
      ApiEndpoints.projectStatus(id),
      body: UpdateStatusRequest.projectStatus(status).toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }

  /// Two calls, because the dependency edges and the live work item state
  /// live on different endpoints (gap `G-DEPS`). A project without a plan
  /// answers `404`, which is a legitimate state — not an error — so it maps
  /// to null and the UI says so explicitly.
  @override
  Future<ProjectPlan?> getPlan(
    String projectId, {
    RequestCancellation? cancellation,
  }) async {
    final token = cancelTokenOf(cancellation);

    final Map<String, dynamic> planJson;
    try {
      planJson = await _client.getJson(
        ApiEndpoints.projectPlan(projectId),
        cancelToken: token,
      );
    } on NotFoundError {
      return null;
    }

    final tasksJson = await _client.getJsonList(
      ApiEndpoints.projectTasks(projectId),
      cancelToken: token,
    );

    final workItems = tasksJson
        .map((e) => mapWorkItem(WorkItemDto.fromJson(e)))
        .toList(growable: false);

    return mapProjectPlan(ProjectPlanDto.fromJson(planJson), workItems);
  }
}
