import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import '../models/project_list_filter.dart';
import 'dashboard_controller.dart';

/// The Projects screen: the full list, client-side filtering and sorting, and
/// the Start action.
///
/// `GET /api/projects` returns every project with no paging, filtering or
/// ordering parameters (gap `G-PROJECT-PAGING`), so all three happen here on
/// the already-retrieved collection. Nothing pretends the server did it.
class ProjectsController extends GetxController {
  ProjectsController({ProjectRepository? projects})
    : _projects = projects ?? Get.find<ProjectRepository>();

  /// Above this many rows the screen suggests filtering, because the list is
  /// unpaged and every project arrives in one response.
  static const int largeListThreshold = 200;

  final ProjectRepository _projects;

  final Rx<ViewState<List<Project>>> state = Rx<ViewState<List<Project>>>(
    const Idle<List<Project>>(),
  );

  final Rx<ProjectListFilter> filter = Rx<ProjectListFilter>(
    ProjectListFilter.none,
  );

  /// Projects whose Start request is in flight. A set rather than a flag
  /// because the list shows a Start button per row, and only the row that
  /// was pressed may be disabled.
  final RxSet<String> startingIds = <String>{}.obs;

  /// The last failed action, shown as a banner above the list. Separate from
  /// [state]: a failed Start must not blank out a list that loaded fine.
  final Rxn<AppError> actionError = Rxn<AppError>();

  /// Set after a successful Start so the screen can confirm what happened
  /// using the status the **server** returned.
  final Rxn<Project> lastStarted = Rxn<Project>();

  RequestCancellation? _inFlight;

  /// The rows actually rendered: filtered, then ordered by the presentation
  /// rule in [ProjectSort].
  List<Project> get visibleProjects {
    final all = state.value.dataOrNull ?? const <Project>[];
    return ProjectSort.apply(all.where(filter.value.matches));
  }

  int get totalCount => state.value.dataOrNull?.length ?? 0;

  bool get isFiltered => filter.value.isActive;

  bool get isLargeList => totalCount > largeListThreshold;

  bool isStarting(String projectId) => startingIds.contains(projectId);

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

  void setStatusFilter(ProjectStatusFilter status) {
    filter.value = filter.value.copyWith(status: status);
  }

  void setQuery(String query) {
    filter.value = filter.value.copyWith(query: query);
  }

  void clearFilter() => filter.value = ProjectListFilter.none;

  void dismissActionError() => actionError.value = null;

  Future<void> _fetch({required bool refreshing}) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    state.value = refreshing && previous != null
        ? Refreshing<List<Project>>(previous)
        : const Loading<List<Project>>();

    try {
      final projects = await _projects.list(cancellation: cancellation);
      if (_inFlight != cancellation) return;
      _inFlight = null;

      state.value = projects.isEmpty
          ? const Empty<List<Project>>()
          : Success<List<Project>>(List<Project>.unmodifiable(projects));
    } on RequestCancelledError {
      // Superseded by a newer request; that request owns the state now.
      return;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return;
      _inFlight = null;
      state.value = Failure<List<Project>>(error, stale: previous);
    }
  }

  /// `POST /api/projects/{id}/start`.
  ///
  /// The backend decides whether the project may start — this method never
  /// pre-judges the lifecycle and never writes a status locally. The project
  /// in the list is replaced with the one the **server** returned, so the
  /// visible status is always the persisted one.
  ///
  /// Duplicate protection: a second call for the same project while the
  /// first is in flight is dropped, so a double-click cannot produce two
  /// requests. (`start` is idempotent server-side as well, which makes a
  /// retry after a timeout safe.)
  Future<void> startProject(String projectId, {String? actor}) async {
    if (startingIds.contains(projectId)) return;

    startingIds.add(projectId);
    actionError.value = null;
    lastStarted.value = null;

    try {
      final started = await _projects.start(projectId, actor: actor);
      _replace(started);
      lastStarted.value = started;
    } on RequestCancelledError {
      return;
    } on AppError catch (error) {
      actionError.value = error;
      // A 409 means the client's copy disagrees with the server; a 404 means
      // it no longer exists. Either way the list is stale, so re-read rather
      // than leaving a row the operator cannot act on.
      if (error is ConflictError || error is NotFoundError) {
        await refreshData();
      }
    } finally {
      startingIds.remove(projectId);
    }
  }

  /// Folds a project the server just returned back into the list, without
  /// re-reading the whole collection.
  void _replace(Project project) {
    final current = state.value.dataOrNull;
    if (current == null) return;

    final updated = <Project>[
      for (final existing in current)
        if (existing.id == project.id) project else existing,
    ];
    state.value = Success<List<Project>>(List<Project>.unmodifiable(updated));
  }

  /// Adds a project created elsewhere (the create dialog) so the list shows
  /// it immediately, then re-reads to stay authoritative.
  Future<void> onProjectCreated(Project project) async {
    final current = state.value.dataOrNull;
    if (current != null) {
      state.value = Success<List<Project>>(
        List<Project>.unmodifiable(<Project>[project, ...current]),
      );
    }
    await refreshData();
  }
}
