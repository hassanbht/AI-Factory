import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import 'dashboard_controller.dart';

/// Backs the Project Detail **placeholder**.
///
/// It reads one endpoint, `GET /api/projects/{id}`, so the placeholder can
/// name the project the operator opened and show the status the server holds
/// for it. Everything else on that screen — lifecycle, work items, runs,
/// review — belongs to Phase 19.4 and later, and the screen says so instead
/// of imitating it.
///
/// The project is fetched rather than passed in, so a deep link to
/// `/projects/{id}` works on its own.
class ProjectDetailController extends GetxController {
  ProjectDetailController({required this.projectId, ProjectRepository? projects})
    : _projects = projects ?? Get.find<ProjectRepository>();

  final String projectId;
  final ProjectRepository _projects;

  final Rx<ViewState<Project>> state = Rx<ViewState<Project>>(
    const Idle<Project>(),
  );

  RequestCancellation? _inFlight;

  @override
  void onInit() {
    super.onInit();
    load();
  }

  @override
  void onClose() {
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  Future<void> load() => _fetch(refreshing: false);

  /// See [DashboardController.refreshData] for why this is not `refresh`.
  Future<void> refreshData() => _fetch(refreshing: true);

  Future<void> _fetch({required bool refreshing}) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    state.value = refreshing && previous != null
        ? Refreshing<Project>(previous)
        : const Loading<Project>();

    try {
      final project = await _projects.getById(
        projectId,
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return;
      _inFlight = null;
      state.value = Success<Project>(project);
    } on RequestCancelledError {
      return;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return;
      _inFlight = null;
      state.value = Failure<Project>(error, stale: previous);
    }
  }
}
