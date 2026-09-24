import 'dart:async' show unawaited;

import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/repositories/repositories.dart';
import '../models/work_items_data.dart';

/// Backs the WorkItems + Dependencies section of Project Detail.
///
/// Two endpoints, joined the way `docs/flutter-architecture.md` §6 documents:
/// `GET .../plan` first — a 404 means no plan exists, not an error, so the
/// section falls back to `GET .../tasks` as a flat list rather than showing
/// an error for a project that simply has not been planned yet.
///
/// Selecting a work item fetches its readiness lazily — one call, for the
/// selected item only — because `GET /api/tasks/{id}/readiness` is a
/// per-item endpoint and the plan's own `readiness` field can be stale by
/// the time the operator looks at a specific item.
class WorkItemsController extends GetxController {
  WorkItemsController({
    required this.projectId,
    ProjectRepository? projects,
    WorkItemRepository? workItems,
  }) : _projects = projects ?? Get.find<ProjectRepository>(),
       _workItems = workItems ?? Get.find<WorkItemRepository>();

  final String projectId;
  final ProjectRepository _projects;
  final WorkItemRepository _workItems;

  final Rx<ViewState<WorkItemsData>> state = Rx<ViewState<WorkItemsData>>(
    const Idle<WorkItemsData>(),
  );

  final Rxn<String> selectedWorkItemId = Rxn<String>();
  final Rx<ViewState<TaskReadiness>> readinessState =
      Rx<ViewState<TaskReadiness>>(const Idle<TaskReadiness>());

  RequestCancellation? _inFlight;
  RequestCancellation? _readinessInFlight;

  @override
  void onInit() {
    super.onInit();
    load();
  }

  @override
  void onClose() {
    _inFlight?.cancel();
    _readinessInFlight?.cancel();
    _inFlight = null;
    _readinessInFlight = null;
    super.onClose();
  }

  Future<void> load() => _fetch(refreshing: false);

  Future<void> refreshData() => _fetch(refreshing: true);

  /// One read inside Project Detail's coordinated refresh cycle. Returns the
  /// error, or null on success; [silent] leaves the section as it is until
  /// the response replaces it. This controller owns no timer of its own.
  Future<AppError?> poll({required bool silent}) =>
      _fetch(refreshing: true, silent: silent);

  /// A work item the Factory is executing right now, as last reported.
  bool get hasActiveWork =>
      state.value.dataOrNull?.workItems.any(
        (item) => switch (item.status) {
          WorkItemStatus.running ||
          WorkItemStatus.testing ||
          WorkItemStatus.repairing ||
          WorkItemStatus.reviewing => true,
          _ => false,
        },
      ) ??
      false;

  /// Selects a work item, or clears the selection when [id] is null.
  void selectWorkItem(String? id) {
    selectedWorkItemId.value = id;
    _readinessInFlight?.cancel();
    _readinessInFlight = null;
    if (id == null) {
      readinessState.value = const Idle<TaskReadiness>();
      return;
    }
    unawaited(_loadReadiness(id));
  }

  Future<AppError?> _fetch({
    required bool refreshing,
    bool silent = false,
  }) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    if (!silent) {
      state.value = refreshing && previous != null
          ? Refreshing<WorkItemsData>(previous)
          : const Loading<WorkItemsData>();
    }

    try {
      final plan = await _projects.getPlan(
        projectId,
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return const RequestCancelledError();

      final data = plan != null && plan.nodes.isNotEmpty
          ? WorkItemsData.withPlan([
              for (final node in plan.nodes) node.workItem,
            ], plan)
          : WorkItemsData.flat(
              await _workItems.listByProject(
                projectId,
                cancellation: cancellation,
              ),
            );
      if (_inFlight != cancellation) return const RequestCancelledError();

      _inFlight = null;
      state.value = data.isEmpty
          ? const Empty<WorkItemsData>()
          : Success<WorkItemsData>(data);
      return null;
    } on RequestCancelledError catch (error) {
      return error;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return const RequestCancelledError();
      _inFlight = null;
      state.value = Failure<WorkItemsData>(error, stale: previous);
      return error;
    }
  }

  Future<void> _loadReadiness(String workItemId) async {
    final cancellation = RequestCancellation();
    _readinessInFlight = cancellation;
    readinessState.value = const Loading<TaskReadiness>();

    try {
      final readiness = await _workItems.getReadiness(
        workItemId,
        cancellation: cancellation,
      );
      if (_readinessInFlight != cancellation) return;
      _readinessInFlight = null;
      readinessState.value = Success<TaskReadiness>(readiness);
    } on RequestCancelledError {
      return;
    } on AppError catch (error) {
      if (_readinessInFlight != cancellation) return;
      _readinessInFlight = null;
      readinessState.value = Failure<TaskReadiness>(error);
    }
  }
}
