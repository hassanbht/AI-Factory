import '../../core/network/api_client.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/audit.dart';
import '../../domain/repositories/repositories.dart';
import '../api/api_endpoints.dart';
import '../dto/audit_dto.dart';
import '../mappers/mappers.dart';

/// Reads the backend's audit log and nothing else.
///
/// The query deliberately carries no `severity`: the endpoint validates that
/// parameter and then drops it before building its own query, so sending it
/// would promise a server-side filter the backend does not apply
/// (`G-AUDIT-SEVERITY`).
class ApiAuditRepository implements AuditRepository {
  const ApiAuditRepository(this._client);

  final ApiClient _client;

  @override
  Future<PagedResult<AuditLogEntry>> list(
    AuditLogQuery query, {
    RequestCancellation? cancellation,
  }) async {
    final json = await _client.getJson(
      ApiEndpoints.auditLog,
      queryParameters: query.toQueryParameters(),
      cancelToken: cancelTokenOf(cancellation),
    );
    return mapAuditLogPage(AuditLogPageDto.fromJson(json));
  }
}
