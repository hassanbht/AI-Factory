import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/polling/app_activity.dart';
import '../../core/polling/polling_coordinator.dart';
import '../../core/polling/polling_policy.dart';
import '../../core/state/view_state.dart';
import '../../domain/models/operations.dart';
import '../../domain/repositories/repositories.dart';

/// Backs the Project Diagnostics screen — one project, one endpoint.
///
/// Deliberately separate from `ProjectDetailController` (Workstream AB):
/// opening diagnostics must not re-read the project, and refreshing
/// diagnostics must not refresh Project Detail. The screen is reached by
/// `projectId` and re-reads the authoritative data itself rather than being
/// handed a diagnostics object through navigation.
class DiagnosticsController extends GetxController {
  DiagnosticsController({
    required this.projectId,
    DiagnosticsRepository? diagnostics,
    this._intervals = PollIntervals.defaults,
    AppActivity? activity,
  }) : _diagnostics = diagnostics ?? Get.find<DiagnosticsRepository>(),
       _activity = activity ?? AppActivity.maybeFind();

  final String projectId;
  final DiagnosticsRepository _diagnostics;
  final PollIntervals _intervals;
  final AppActivity? _activity;

  final Rx<ViewState<ProjectDiagnostics>> state =
      Rx<ViewState<ProjectDiagnostics>>(const Idle<ProjectDiagnostics>());

  /// Live updates while the project can still change:
  /// [PollIntervals.diagnostics] while it is being advanced or has an agent
  /// run in flight, [PollIntervals.idle] while it waits, and not at all once
  /// it is `Completed`, `Failed` or `Cancelled`. A `401` stops polling until
  /// the operator enters a key — the rejected one is never re-sent on a timer.
  late final PollingCoordinator live = PollingCoordinator(
    task: (trigger) => _fetch(refreshing: true, silent: trigger.isBackground),
    interval: _interval,
    maxBackoff: _intervals.maxBackoff,
    activity: _activity,
  );

  RequestCancellation? _inFlight;

  /// `401`: the endpoint is behind the operational key and this client has
  /// no valid one. A distinct screen state, not a server failure — retrying
  /// without a key produces the same answer.
  bool get needsOperatorAccess {
    final current = state.value;
    return current is Failure<ProjectDiagnostics> &&
        current.error is AuthenticationError;
  }

  /// `404`: the backend has no diagnostics for this project id.
  bool get isNotFound {
    final current = state.value;
    return current is Failure<ProjectDiagnostics> &&
        current.error is NotFoundError;
  }

  @override
  void onInit() {
    super.onInit();
    load();
  }

  @override
  void onClose() {
    live.dispose();
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  /// First load, retry, and the re-read after a key is entered. Through
  /// [live], so it never overlaps a poll and it resumes polling on success.
  Future<void> load() => live.refreshNow();

  /// Operator-initiated refresh; also [live]'s manual path.
  Future<void> refreshData() => live.refreshNow();

  Duration? _interval() {
    final data = state.value.dataOrNull;
    if (data == null) return _intervals.diagnostics;
    if (data.projectStatus.isTerminal) return null;
    if (data.projectStatus.isActive || data.activeAgentRuns > 0) {
      return _intervals.diagnostics;
    }
    return _intervals.idle;
  }

  /// Returns the error that decides the next poll, or null on success.
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
          ? Refreshing<ProjectDiagnostics>(previous)
          : const Loading<ProjectDiagnostics>();
    }

    try {
      final result = await _diagnostics.getProjectDiagnostics(
        projectId,
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return const RequestCancelledError();
      state.value = Success<ProjectDiagnostics>(result);
      return null;
    } on RequestCancelledError catch (error) {
      return error;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return const RequestCancelledError();
      state.value = Failure<ProjectDiagnostics>(error, stale: previous);
      return error;
    } finally {
      if (_inFlight == cancellation) _inFlight = null;
    }
  }
}
