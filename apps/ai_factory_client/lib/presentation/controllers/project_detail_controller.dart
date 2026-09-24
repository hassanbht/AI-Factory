import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/polling/app_activity.dart';
import '../../core/polling/polling_coordinator.dart';
import '../../core/polling/polling_policy.dart';
import '../../core/state/view_state.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import '../models/project_lifecycle.dart';
import 'agent_runs_controller.dart';
import 'dashboard_controller.dart';
import 'work_items_controller.dart';

/// Backs the Project Detail screen.
///
/// It reads one endpoint, `GET /api/projects/{id}`, and writes one,
/// `POST /api/projects/{id}/start`. Work items and agent runs have their own
/// controllers; this one only schedules them, so the whole screen refreshes
/// in one coordinated cycle (Phase 19.9) rather than on three timers.
///
/// The project is fetched by id rather than handed over by the list, so a
/// deep link to `/projects/{id}` works on its own and the screen always shows
/// the state the server currently holds — not a copy captured when the list
/// was last read.
class ProjectDetailController extends GetxController {
  ProjectDetailController({
    required this.projectId,
    ProjectRepository? projects,
    this._workItems,
    this._agentRuns,
    this._intervals = PollIntervals.defaults,
    AppActivity? activity,
  }) : _projects = projects ?? Get.find<ProjectRepository>(),
       _activity = activity ?? AppActivity.maybeFind();

  final String projectId;
  final ProjectRepository _projects;

  /// Sibling sections refreshed in the same cycle as the project. Optional so
  /// the project alone can be exercised; the route binding supplies both.
  final WorkItemsController? _workItems;
  final AgentRunsController? _agentRuns;

  final PollIntervals _intervals;
  final AppActivity? _activity;

  /// The screen's **one** polling schedule. Project, work items and agent
  /// runs are re-read together in a single cycle — never three timers — at an
  /// interval chosen from what the server last reported:
  ///
  /// * a queued or executing agent run — [PollIntervals.agentRuns];
  /// * a project the Factory is advancing, or a work item in flight —
  ///   [PollIntervals.projectDetail];
  /// * `HumanReview` — [PollIntervals.humanReview], so another operator's
  ///   approval is noticed;
  /// * `Created`, `Paused` — [PollIntervals.idle];
  /// * `Completed`, `Failed`, `Cancelled` — not at all. A manual refresh
  ///   still works.
  late final PollingCoordinator live = PollingCoordinator(
    task: _cycle,
    interval: _interval,
    maxBackoff: _intervals.maxBackoff,
    activity: _activity,
  );

  final Rx<ViewState<Project>> state = Rx<ViewState<Project>>(
    const Idle<Project>(),
  );

  /// A Start request is in flight. Separate from [state.isBusy], which covers
  /// reads: the screen keeps showing the project while the action runs.
  final RxBool isStarting = false.obs;

  /// The last failed **action**, shown as a banner above the content. A
  /// rejected Start must not blank out a project that loaded fine, so it is
  /// never folded into [state].
  final Rxn<AppError> actionError = Rxn<AppError>();

  RequestCancellation? _inFlight;

  Project? get project => state.value.dataOrNull;

  /// `404` from `GET /api/projects/{id}`: the project does not exist. A
  /// distinct screen state, not a server failure — retrying will not bring it
  /// back, so the screen offers a way back to the list instead.
  bool get isNotFound {
    final current = state.value;
    return current is Failure<Project> &&
        current.error is NotFoundError &&
        current.stale == null;
  }

  /// Whether the Start action is offered.
  ///
  /// This mirrors the backend's documented precondition rather than inventing
  /// one: `ProjectLifecycleOrchestrator.StartAsync` accepts a project only
  /// when `lifecycleStartedAtUtc` is null **and** the status is `Analyzing` —
  /// which is precisely "a source decision was made and succeeded". Every
  /// other state is answered with a `409`, so offering the button there would
  /// be offering a guaranteed failure.
  ///
  /// It remains a display rule, not a lifecycle decision: the client never
  /// writes a status, and a `409` from the server is still shown if the two
  /// disagree.
  bool get canStart {
    final current = project;
    return current != null &&
        !current.isStarted &&
        current.status == ProjectStatus.analyzing;
  }

  /// The lifecycle derived from the status the server reported, or null while
  /// nothing has loaded.
  ProjectLifecycle? get lifecycle {
    final current = project;
    return current == null ? null : ProjectLifecycle.of(current);
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

  /// The first load, and the retry after a failed one. Goes through [live],
  /// so it can never overlap a poll.
  Future<void> load() => live.refreshNow();

  /// See [DashboardController.refreshData] for why this is not `refresh`.
  ///
  /// Runs the whole coordinated cycle — project, work items, agent runs —
  /// now, or right after the one in flight. It keeps the project on screen
  /// and never blanks the page. Used by the refresh button, after Start and
  /// after an approval.
  Future<void> refreshData() => live.refreshNow();

  void dismissActionError() => actionError.value = null;

  Duration? _interval() {
    final current = project;
    if (current == null) return _intervals.projectDetail;
    if (current.status.isTerminal) return null;
    if (_agentRuns?.hasActiveRuns ?? false) return _intervals.agentRuns;
    if (current.status.isActive || (_workItems?.hasActiveWork ?? false)) {
      return _intervals.projectDetail;
    }
    if (current.status == ProjectStatus.humanReview) {
      return _intervals.humanReview;
    }
    return _intervals.idle;
  }

  /// One refresh cycle for the whole screen. A section still on its own
  /// first load is left alone rather than read twice.
  Future<AppError?> _cycle(PollTrigger trigger) async {
    final silent = trigger.isBackground;
    bool settled(ViewState<Object?> state) =>
        state is! Idle<Object?> && state is! Loading<Object?>;

    final results = await Future.wait<AppError?>(<Future<AppError?>>[
      _fetch(refreshing: true, silent: silent),
      if (_workItems case final workItems? when settled(workItems.state.value))
        workItems.poll(silent: silent),
      if (_agentRuns case final agentRuns? when settled(agentRuns.state.value))
        agentRuns.poll(silent: silent),
    ]);
    return PollOutcome.decisive(
      primary: results.first,
      secondary: results.skip(1),
    );
  }

  /// Returns the error that decides the next poll, or null on success. A
  /// [silent] read is a background poll and shows no progress line.
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
          ? Refreshing<Project>(previous)
          : const Loading<Project>();
    }

    try {
      final project = await _projects.getById(
        projectId,
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return const RequestCancelledError();
      _inFlight = null;
      state.value = Success<Project>(project);
      return null;
    } on RequestCancelledError catch (error) {
      // Superseded by a newer request; that request owns the state now.
      return error;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return const RequestCancelledError();
      _inFlight = null;
      // A 404 replaces the project rather than shadowing it: a project that
      // no longer exists must not keep rendering from a stale copy.
      state.value = error is NotFoundError
          ? Failure<Project>(error)
          : Failure<Project>(error, stale: previous);
      return error;
    }
  }

  /// `POST /api/projects/{id}/start`.
  ///
  /// The status on screen afterwards is the one the **server** returned. No
  /// status is written locally, before or after the call — a project the
  /// client believes it started but the server rejected is exactly the lie
  /// this console exists to avoid.
  ///
  /// Duplicate protection: a second call while the first is in flight is
  /// dropped, so a double press cannot produce two requests. (`start` is
  /// idempotent server-side too, which makes a retry after a timeout safe.)
  Future<void> startProject({String? actor}) async {
    if (isStarting.value) return;

    isStarting.value = true;
    actionError.value = null;

    try {
      final started = await _projects.start(projectId, actor: actor);
      state.value = Success<Project>(started);
      // Re-read the whole screen at once — work items and runs appear as the
      // Factory creates them — instead of waiting for the next poll.
      await refreshData();
    } on RequestCancelledError {
      return;
    } on AppError catch (error) {
      actionError.value = error;
      // A 409 means the client's copy disagrees with the server and a 404
      // means it is gone. Either way what is on screen is stale, so re-read
      // rather than leave an action the operator cannot complete.
      if (error is ConflictError || error is NotFoundError) {
        await refreshData();
      }
    } finally {
      isStarting.value = false;
    }
  }
}
