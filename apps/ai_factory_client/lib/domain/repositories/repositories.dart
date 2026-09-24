/// The data-access contracts the application depends on.
///
/// Controllers depend on these interfaces, never on `ApiClient` and never on
/// Dio. Every method returns domain models and throws an `AppError`; no DTO
/// and no `DioException` crosses this boundary.
///
/// Every read accepts an optional [RequestCancellation] so a screen can abort
/// its in-flight requests on dispose — the same mechanism polling uses.
library;

import '../../core/network/request_cancellation.dart';
import '../../data/dto/requests.dart';
import '../enums/backend_enums.dart';
import '../models/agent_run.dart';
import '../models/audit.dart';
import '../models/operations.dart';
import '../models/project.dart';
import '../models/work_item.dart';

abstract class ProjectRepository {
  /// `GET /api/projects`. Unpaged: the endpoint returns every project
  /// (gap `G-PROJECT-PAGING`).
  Future<List<Project>> list({RequestCancellation? cancellation});

  /// `GET /api/projects/{id}`. Throws `NotFoundError` when it does not exist.
  Future<Project> getById(String id, {RequestCancellation? cancellation});

  /// `POST /api/projects`. The project starts in `Created`; the source must
  /// be initialized before it can be started.
  Future<Project> create(
    CreateProjectRequest request, {
    RequestCancellation? cancellation,
  });

  /// `POST /api/projects/{id}/source`. Moves `Created` -> `Analyzing`.
  /// Rejected with `ConflictError` once the source has been initialized.
  Future<Project> initializeSource(
    String id,
    InitializeProjectSourceRequest request, {
    RequestCancellation? cancellation,
  });

  /// `POST /api/projects/{id}/start`. Idempotent: an already-started project
  /// comes back unchanged rather than starting a second execution.
  Future<Project> start(
    String id, {
    String? actor,
    RequestCancellation? cancellation,
  });

  /// `PATCH /api/projects/{id}/status`.
  ///
  /// The backend rejects `Completed` here under every input — completion is
  /// [ApprovalRepository.approve] and nothing else. Used for pause, resume,
  /// cancel and the `HumanReview -> Implementation` rework transition.
  Future<Project> updateStatus(
    String id,
    ProjectStatus status, {
    RequestCancellation? cancellation,
  });

  /// `GET /api/projects/{projectId}/plan` joined with the project's work
  /// items, which is the only way to obtain dependency edges (gap `G-DEPS`).
  /// Null when the project has no plan yet.
  Future<ProjectPlan?> getPlan(
    String projectId, {
    RequestCancellation? cancellation,
  });
}

abstract class WorkItemRepository {
  /// `GET /api/projects/{projectId}/tasks`.
  Future<List<WorkItem>> listByProject(
    String projectId, {
    RequestCancellation? cancellation,
  });

  /// `GET /api/tasks/{id}`.
  Future<WorkItem> getById(String id, {RequestCancellation? cancellation});

  /// `GET /api/tasks/{id}/readiness` — why this item can or cannot run now.
  /// One call per item, so callers fetch it for the selected item only.
  Future<TaskReadiness> getReadiness(
    String id, {
    RequestCancellation? cancellation,
  });

  /// `PATCH /api/tasks/{id}/status`.
  Future<WorkItem> updateStatus(
    String id,
    WorkItemStatus status, {
    RequestCancellation? cancellation,
  });
}

abstract class AgentRunRepository {
  /// `GET /api/agents/runs` — the only paged listing in the API. There is no
  /// `workItemId` filter, so callers filter client-side (gap
  /// `G-WORKITEM-EVENTS`).
  Future<PagedResult<AgentRun>> list(
    AgentRunQuery query, {
    RequestCancellation? cancellation,
  });

  /// `GET /api/agents/runs/{id}`.
  Future<AgentRun> getById(String id, {RequestCancellation? cancellation});

  /// `POST /api/agents/runs` — subject to the expensive-operation rate limit.
  Future<AgentRun> create(
    CreateAgentRunRequest request, {
    RequestCancellation? cancellation,
  });

  /// `POST /api/agents/runs/{id}/cancel`. Accepted asynchronously: the run
  /// reaches `Cancelled` later, so callers re-read rather than assume.
  Future<void> cancelRun(String id, {RequestCancellation? cancellation});

  /// `GET /api/agents/definitions`.
  Future<List<AgentDefinition>> listDefinitions({
    RequestCancellation? cancellation,
  });
}

abstract class DiagnosticsRepository {
  /// `GET /api/operations/projects/{projectId}/diagnostics`.
  ///
  /// Behind the operational key when the backend requires one, in which case
  /// it answers `401` and this throws `AuthenticationError`.
  Future<ProjectDiagnostics> getProjectDiagnostics(
    String projectId, {
    RequestCancellation? cancellation,
  });

  /// `GET /api/agents/metrics` — global counters.
  ///
  /// Not behind the operational key: the backend's operational path check
  /// does not cover `/api/agents/metrics`.
  Future<AgentMetrics> getAgentMetrics({RequestCancellation? cancellation});
}

/// Read-only access to the backend's operational audit log.
///
/// There is no write path: audit entries are written by the backend itself,
/// and this client only reads them.
abstract class AuditRepository {
  /// `GET /api/audit-log`, paged.
  ///
  /// Behind the operational key when the backend requires one. Severity is
  /// not a server-side filter (gap `G-AUDIT-SEVERITY`), so [AuditLogQuery]
  /// does not offer one.
  Future<PagedResult<AuditLogEntry>> list(
    AuditLogQuery query, {
    RequestCancellation? cancellation,
  });
}

/// Read-only operational information. The API exposes no mutation here and
/// neither does this interface.
abstract class OperationsRepository {
  /// `/health/live` and `/health/ready`. Plain text, so this reports only
  /// alive/ready — no per-check detail exists (gap `G-HEALTH-DETAIL`).
  Future<SystemHealth> getHealth({RequestCancellation? cancellation});

  /// `GET /api/operations/deployment` (operational key required).
  Future<DeploymentInfo> getDeployment({RequestCancellation? cancellation});

  /// `GET /api/operations/database` (operational key required).
  Future<DatabaseInfo> getDatabase({RequestCancellation? cancellation});

  /// `GET /api/operations/configuration` (operational key required).
  Future<ConfigurationInfo> getConfiguration({
    RequestCancellation? cancellation,
  });

  /// `GET /api/scheduler/status` — no operational key required.
  Future<SchedulerStatus> getSchedulerStatus({
    RequestCancellation? cancellation,
  });
}

/// The human-approval boundary.
///
/// This is the only completion mechanism in the client. There is no local
/// approval, no direct status write to `Completed`, and no database access —
/// the backend owns the decision and rejects everything else.
abstract class ApprovalRepository {
  /// `POST /api/projects/{id}/approve`.
  ///
  /// Succeeds only from `HumanReview`; every other state is rejected with
  /// `ConflictError` naming the actual status. Idempotent: an
  /// already-completed project is returned unchanged.
  ///
  /// [actor] is an unauthenticated audit label, not a verified identity.
  Future<Project> approve(
    String projectId, {
    String? actor,
    RequestCancellation? cancellation,
  });
}
