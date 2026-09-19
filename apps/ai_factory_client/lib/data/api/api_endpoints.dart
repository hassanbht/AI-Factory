/// Every AI Factory API path this client calls.
///
/// Paths live here and nowhere else, so a backend route change is a one-file
/// edit. Each entry corresponds to a route verified against the running
/// `v1.0.0` API — see `docs/flutter-api-contract.md`.
class ApiEndpoints {
  const ApiEndpoints._();

  // --- Health (no operational key) ---------------------------------------
  static const String healthLive = '/health/live';
  static const String healthReady = '/health/ready';

  // --- Projects -----------------------------------------------------------
  static const String projects = '/api/projects';

  static String project(String id) => '/api/projects/$id';

  static String projectSource(String id) => '/api/projects/$id/source';

  static String projectStart(String id) => '/api/projects/$id/start';

  static String projectStatus(String id) => '/api/projects/$id/status';

  /// The sole human-approval authority. The client never sets `Completed`
  /// through [projectStatus]; the backend rejects that by design.
  static String projectApprove(String id) => '/api/projects/$id/approve';

  static String projectPlan(String id) => '/api/projects/$id/plan';

  // --- WorkItems (routed as "tasks") --------------------------------------
  static String projectTasks(String projectId) =>
      '/api/projects/$projectId/tasks';

  static String task(String id) => '/api/tasks/$id';

  static String taskStatus(String id) => '/api/tasks/$id/status';

  static String taskReadiness(String id) => '/api/tasks/$id/readiness';

  // --- Agents -------------------------------------------------------------
  static const String agentDefinitions = '/api/agents/definitions';

  static String agentDefinition(String id) => '/api/agents/definitions/$id';

  static const String agentRuns = '/api/agents/runs';

  static String agentRun(String id) => '/api/agents/runs/$id';

  static String agentRunCancel(String id) => '/api/agents/runs/$id/cancel';

  /// Operational key required.
  static const String agentMetrics = '/api/agents/metrics';

  // --- Scheduler ----------------------------------------------------------
  static const String schedulerStatus = '/api/scheduler/status';

  // --- Operations (operational key required) ------------------------------
  static String projectDiagnostics(String projectId) =>
      '/api/operations/projects/$projectId/diagnostics';

  static const String deployment = '/api/operations/deployment';
  static const String database = '/api/operations/database';
  static const String configuration = '/api/operations/configuration';
}
