import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import '../models/dashboard_snapshot.dart';
import '../models/project_list_filter.dart';

/// Coordinates the Dashboard's four independent reads.
///
///     DashboardController
///         -> ProjectRepository      GET /api/projects
///         -> OperationsRepository   /health/live + /health/ready
///                                   GET /api/scheduler/status
///         -> DiagnosticsRepository  GET /api/agents/metrics   (ops key)
///
/// No Dio, no URL and no DTO appears here: the controller only ever talks to
/// repository interfaces resolved from the DI container.
///
/// The project list is the screen's backbone — if it fails there is nothing
/// to show, so the state becomes [Failure]. The other three are additive: one
/// of them failing produces [Partial], which keeps the project data on screen
/// and labels the tile that could not be filled.
class DashboardController extends GetxController {
  DashboardController({
    ProjectRepository? projects,
    OperationsRepository? operations,
    DiagnosticsRepository? diagnostics,
  }) : _projects = projects ?? Get.find<ProjectRepository>(),
       _operations = operations ?? Get.find<OperationsRepository>(),
       _diagnostics = diagnostics ?? Get.find<DiagnosticsRepository>();

  final ProjectRepository _projects;
  final OperationsRepository _operations;
  final DiagnosticsRepository _diagnostics;

  final Rx<ViewState<DashboardSnapshot>> state =
      Rx<ViewState<DashboardSnapshot>>(const Idle<DashboardSnapshot>());

  RequestCancellation? _inFlight;

  @override
  void onInit() {
    super.onInit();
    load();
  }

  @override
  void onClose() {
    // Leaving the screen aborts whatever is still running, so a late response
    // cannot write into a disposed controller.
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  /// First load. Shows a skeleton because there is nothing to keep on screen.
  Future<void> load() => _fetch(refreshing: false);

  /// Operator-triggered refresh. Existing content stays visible.
  ///
  /// Named `refreshData` rather than `refresh` because `GetxController`
  /// already has a `refresh()` that notifies `GetBuilder` listeners; shadowing
  /// it with an async reload would be a trap for the next screen.
  Future<void> refreshData() => _fetch(refreshing: true);

  Future<void> _fetch({required bool refreshing}) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    state.value = refreshing && previous != null
        ? Refreshing<DashboardSnapshot>(previous)
        : const Loading<DashboardSnapshot>();

    // One round trip rather than four sequential ones. Each result is
    // captured independently so a single failure cannot cancel the others.
    final results = await Future.wait(<Future<Object?>>[
      _guard(() => _projects.list(cancellation: cancellation)),
      _guard(() => _operations.getHealth(cancellation: cancellation)),
      _guard(() => _operations.getSchedulerStatus(cancellation: cancellation)),
      _guard(() => _diagnostics.getAgentMetrics(cancellation: cancellation)),
    ]);

    if (cancellation.isCancelled || _inFlight != cancellation) return;
    _inFlight = null;

    final projectsResult = results[0];
    if (projectsResult is AppError) {
      if (projectsResult is RequestCancelledError) return;
      state.value = Failure<DashboardSnapshot>(
        projectsResult,
        stale: previous,
      );
      return;
    }

    final projects = projectsResult! as List<Project>;
    final failures = <AppError>[];

    final health = _unwrap<SystemHealth>(results[1], failures);
    final scheduler = _unwrap<SchedulerStatus>(results[2], failures);

    // A 401 here is not a failure of the Dashboard: the operational key is
    // simply not configured, which is the normal state until Phase 19.8.
    // It renders as an explicit "requires an operational key" note rather
    // than as an error the operator is invited to retry.
    final metricsResult = results[3];
    final requiresKey = metricsResult is AuthenticationError;
    final metrics = requiresKey
        ? null
        : _unwrap<AgentMetrics>(metricsResult, failures);

    final snapshot = DashboardSnapshot(
      counts: ProjectStatusCounts.from(projects),
      needsAttention: ProjectSort.needsAttention(
        projects,
        take: DashboardSnapshot.bandLimit,
      ),
      recent: ProjectSort.mostRecent(
        projects,
        take: DashboardSnapshot.bandLimit,
      ),
      health: health,
      scheduler: scheduler,
      agentMetrics: metrics,
      agentMetricsRequiresKey: requiresKey,
    );

    state.value = failures.isEmpty
        ? Success<DashboardSnapshot>(snapshot)
        : Partial<DashboardSnapshot>(snapshot, failures);
  }

  /// Runs [action] and returns either its value or the [AppError] it threw,
  /// so `Future.wait` never short-circuits on the first failing source.
  Future<Object?> _guard(Future<Object?> Function() action) async {
    try {
      return await action();
    } on AppError catch (error) {
      return error;
    }
  }

  /// Returns the value when the source succeeded, or records its error and
  /// returns null. A null value is what makes the corresponding tile render
  /// "Not available" instead of a number nobody can vouch for.
  T? _unwrap<T>(Object? result, List<AppError> failures) {
    if (result is T) return result;
    if (result is AppError && result is! RequestCancelledError) {
      failures.add(result);
    }
    return null;
  }
}

