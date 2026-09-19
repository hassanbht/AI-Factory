import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/project_dto.dart';
import '../dto/requests.dart';
import '../mappers/mappers.dart';

/// The single completion path in this client.
///
/// It calls the backend's approval endpoint and does nothing else: it never
/// writes `Completed` through the status endpoint (the backend rejects that
/// by design), never records an approval locally, and never touches
/// persistence. If the project is not in `HumanReview`, the backend answers
/// `409` and that conflict is surfaced rather than worked around.
class ApiApprovalRepository implements ApprovalRepository {
  const ApiApprovalRepository(this._client);

  final ApiClient _client;

  @override
  Future<Project> approve(
    String projectId, {
    String? actor,
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.postJson(
      ApiEndpoints.projectApprove(projectId),
      body: ActorRequest(actor: actor).toJson(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapProject(ProjectDto.fromJson(json));
  }
}
