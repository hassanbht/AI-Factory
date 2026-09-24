import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/polling/app_activity.dart';
import '../../core/polling/polling_coordinator.dart';
import '../../core/polling/polling_policy.dart';
import '../../core/state/view_state.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import '../../domain/repositories/repositories.dart';
import 'operator_access_controller.dart';

/// Backs the Operations screen.
///
/// Operations is six independent endpoints, not one aggregate, so it keeps
/// **six independent states** rather than a single `ViewState` (Workstream Z):
/// agent metrics answering `401` must leave health, deployment, database,
/// scheduler and configuration exactly as they were. Every section therefore
/// loads, fails, retries and refreshes on its own, and the screen stays
/// useful whenever any one of them succeeded.
///
/// Live updates (Phase 19.9) re-read only what changes on its own — health,
/// scheduler activity and agent metrics — every [PollIntervals.operations].
/// Deployment, database and configuration are effectively static, so they
/// are read on open and on a manual refresh, never on a timer.
class OperationsController extends GetxController {
  OperationsController({
    OperationsRepository? operations,
    DiagnosticsRepository? diagnostics,
    OperatorAccessController? access,
    this._intervals = PollIntervals.defaults,
    AppActivity? activity,
  }) : _operations = operations ?? Get.find<OperationsRepository>(),
       _diagnostics = diagnostics ?? Get.find<DiagnosticsRepository>(),
       _access = access ?? Get.find<OperatorAccessController>(),
       _activity = activity ?? AppActivity.maybeFind();

  final OperationsRepository _operations;
  final DiagnosticsRepository _diagnostics;
  final OperatorAccessController _access;
  final PollIntervals _intervals;
  final AppActivity? _activity;

  /// A manual cycle reads all six sections; a background one reads the three
  /// that change, skipping any that failed permanently (a `404` will not fix
  /// itself). Each section still succeeds or fails on its own.
  late final PollingCoordinator live = PollingCoordinator(
    task: (trigger) =>
        trigger.isBackground ? _poll() : _fetchAll(refreshing: true),
    interval: () => _intervals.operations,
    maxBackoff: _intervals.maxBackoff,
    activity: _activity,
  );

  final Rx<ViewState<SystemHealth>> health = Rx<ViewState<SystemHealth>>(
    const Idle<SystemHealth>(),
  );
  final Rx<ViewState<DeploymentInfo>> deployment =
      Rx<ViewState<DeploymentInfo>>(const Idle<DeploymentInfo>());
  final Rx<ViewState<DatabaseInfo>> database = Rx<ViewState<DatabaseInfo>>(
    const Idle<DatabaseInfo>(),
  );
  final Rx<ViewState<ConfigurationInfo>> configuration =
      Rx<ViewState<ConfigurationInfo>>(const Idle<ConfigurationInfo>());
  final Rx<ViewState<SchedulerStatus>> scheduler =
      Rx<ViewState<SchedulerStatus>>(const Idle<SchedulerStatus>());
  final Rx<ViewState<AgentMetrics>> metrics = Rx<ViewState<AgentMetrics>>(
    const Idle<AgentMetrics>(),
  );

  RequestCancellation? _inFlight;

  /// Guards the one credential retry per load pass (Workstream K): a `401`
  /// re-reads the stored key and retries once, and a second `401` is final.
  /// Without this a wrong key would retry forever.
  bool _retriedAfterUnauthorized = false;

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

  /// True when at least one section is unreachable for lack of a valid
  /// operational key — which is what the operator-access panel reacts to.
  bool get needsOperatorAccess =>
      <ViewState<Object>>[
        health.value,
        deployment.value,
        database.value,
        configuration.value,
        scheduler.value,
        metrics.value,
      ].any(
        (state) =>
            state is Failure<Object> && state.error is AuthenticationError,
      );

  /// First load — all six sections, through [live] so it never overlaps a
  /// poll.
  Future<void> load() => live.refreshNow();

  /// Manual refresh. Sections that already hold data keep showing it while
  /// the new read is in flight, and a section that fails this time keeps its
  /// previous value as stale rather than blanking (Workstream AA).
  Future<void> refreshData() => live.refreshNow();

  /// The background cycle: only the sections that change on their own, and
  /// none that already failed permanently. Silent — no section flips to
  /// "refreshing" — and each result replaces its section independently.
  Future<AppError?> _poll() async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;
    _retriedAfterUnauthorized = false;

    bool pollable<T>(Rx<ViewState<T>> section) {
      final current = section.value;
      return !(current is Failure<T> &&
          PollOutcome.of(current.error) == PollOutcome.permanent);
    }

    final results = await Future.wait<AppError?>(<Future<AppError?>>[
      if (pollable(health))
        _section<SystemHealth>(
          state: health,
          read: () => _operations.getHealth(cancellation: cancellation),
          cancellation: cancellation,
          refreshing: true,
          silent: true,
        ),
      if (pollable(scheduler))
        _section<SchedulerStatus>(
          state: scheduler,
          read: () =>
              _operations.getSchedulerStatus(cancellation: cancellation),
          cancellation: cancellation,
          refreshing: true,
          silent: true,
        ),
      if (pollable(metrics))
        _section<AgentMetrics>(
          state: metrics,
          read: () => _diagnostics.getAgentMetrics(cancellation: cancellation),
          cancellation: cancellation,
          refreshing: true,
          silent: true,
        ),
    ]);

    if (_inFlight == cancellation) _inFlight = null;
    // Nothing was read: neither a success nor a failure.
    if (results.isEmpty) return const RequestCancelledError();
    return PollOutcome.decisive(secondary: results);
  }

  Future<AppError?> _fetchAll({required bool refreshing}) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;
    _retriedAfterUnauthorized = false;

    // Six independent reads issued together. `Future.wait` would abort the
    // lot on the first error, so each section owns its own try/catch and the
    // futures only ever complete normally.
    final results = await Future.wait<AppError?>(<Future<AppError?>>[
      _section<SystemHealth>(
        state: health,
        read: () => _operations.getHealth(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
      _section<DeploymentInfo>(
        state: deployment,
        read: () => _operations.getDeployment(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
      _section<DatabaseInfo>(
        state: database,
        read: () => _operations.getDatabase(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
      _section<ConfigurationInfo>(
        state: configuration,
        read: () => _operations.getConfiguration(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
      _section<SchedulerStatus>(
        state: scheduler,
        read: () => _operations.getSchedulerStatus(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
      _section<AgentMetrics>(
        state: metrics,
        read: () => _diagnostics.getAgentMetrics(cancellation: cancellation),
        cancellation: cancellation,
        refreshing: refreshing,
      ),
    ]);

    if (_inFlight == cancellation) _inFlight = null;
    return PollOutcome.decisive(secondary: results);
  }

  /// Retries one section without touching the other five.
  Future<void> retryHealth() => _retry<SystemHealth>(
    health,
    (c) => _operations.getHealth(cancellation: c),
  );

  Future<void> retryDeployment() => _retry<DeploymentInfo>(
    deployment,
    (c) => _operations.getDeployment(cancellation: c),
  );

  Future<void> retryDatabase() => _retry<DatabaseInfo>(
    database,
    (c) => _operations.getDatabase(cancellation: c),
  );

  Future<void> retryConfiguration() => _retry<ConfigurationInfo>(
    configuration,
    (c) => _operations.getConfiguration(cancellation: c),
  );

  Future<void> retryScheduler() => _retry<SchedulerStatus>(
    scheduler,
    (c) => _operations.getSchedulerStatus(cancellation: c),
  );

  Future<void> retryMetrics() => _retry<AgentMetrics>(
    metrics,
    (c) => _diagnostics.getAgentMetrics(cancellation: c),
  );

  Future<void> _retry<T>(
    Rx<ViewState<T>> state,
    Future<T> Function(RequestCancellation) read,
  ) async {
    final cancellation = RequestCancellation();
    _retriedAfterUnauthorized = false;
    await _section<T>(
      state: state,
      read: () => read(cancellation),
      cancellation: cancellation,
      refreshing: state.value.dataOrNull != null,
    );
  }

  /// One section's whole lifecycle, including the single post-`401` retry.
  ///
  /// A `401` means the request reached the server and was refused, so the
  /// stored key is re-read (it may have been entered in the meantime) and the
  /// read is attempted exactly once more. A second `401` is reported as
  /// [AuthenticationError] and the operator is asked for a key — this never
  /// loops.
  ///
  /// Returns the section's error, or null on success. A [silent] read is a
  /// background poll: the section keeps what it shows until the response
  /// replaces it.
  Future<AppError?> _section<T>({
    required Rx<ViewState<T>> state,
    required Future<T> Function() read,
    required RequestCancellation cancellation,
    required bool refreshing,
    bool silent = false,
  }) async {
    final previous = state.value.dataOrNull;
    if (!silent) {
      state.value = refreshing && previous != null
          ? Refreshing<T>(previous)
          : Loading<T>();
    }

    try {
      final result = await read();
      // The screen may have been left while this was in flight; publishing
      // now would resurrect a disposed section.
      if (cancellation.isCancelled) return const RequestCancelledError();
      state.value = Success<T>(result);
      return null;
    } on RequestCancelledError catch (error) {
      // The screen was left; whatever this section held is irrelevant now.
      return error;
    } on AuthenticationError catch (error) {
      final recovered = await _retryOnceWithStoredKey<T>(read);
      if (cancellation.isCancelled) return const RequestCancelledError();
      if (recovered == null) {
        state.value = Failure<T>(error, stale: previous);
        return error;
      }
      state.value = Success<T>(recovered);
      return null;
    } on AppError catch (error) {
      if (cancellation.isCancelled) return const RequestCancelledError();
      state.value = Failure<T>(error, stale: previous);
      return error;
    }
  }

  /// Re-applies the stored key and retries [read] once. Returns null when
  /// there is nothing to retry with, or when the retry also failed.
  Future<T?> _retryOnceWithStoredKey<T>(Future<T> Function() read) async {
    if (_retriedAfterUnauthorized) return null;
    _retriedAfterUnauthorized = true;

    await _access.restore();
    if (!_access.hasKey.value) return null;

    try {
      return await read();
    } on AppError {
      return null;
    }
  }
}
