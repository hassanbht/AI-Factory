import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:get/get.dart' hide Trans;

import '../errors/app_error.dart';
import '../utils/app_clock.dart';
import 'app_activity.dart';
import 'polling_policy.dart';

/// One refresh cycle. Returns the error that should drive scheduling (see
/// [PollOutcome.decisive]), or null when the cycle succeeded.
///
/// The task owns the screen state: it fetches, maps and replaces the
/// authoritative data itself. The coordinator only decides **when** it runs.
typedef PollTask = Future<AppError?> Function(PollTrigger trigger);

/// Independent reasons to stop ticking. Both must clear before polling
/// resumes, so returning to the foreground does not revive a screen that is
/// still covered by another route.
enum PollPause {
  /// Another route is on top of this screen.
  hidden,

  /// The application is minimized or otherwise not visible.
  background,
}

/// What the live-update indicator shows. There is deliberately no
/// "connected": polling holds no connection, so a successful poll means
/// "recently refreshed" and nothing more.
enum LiveStatus {
  /// Nothing has loaded yet.
  idle,

  /// The last refresh succeeded and another one is scheduled.
  live,

  /// A refresh is in flight.
  refreshing,

  /// The server could not be reached; retrying with backoff.
  offline,

  /// The server answered with an error; retrying with backoff.
  retrying,

  /// Hidden or in the background; resumes with an immediate refresh.
  paused,

  /// `401`. Stopped until the operator supplies a credential.
  accessRequired,

  /// A permanent error. Stopped until the operator acts.
  halted,

  /// Nothing on screen is changing (a terminal project), so nothing is
  /// scheduled. A manual refresh still works.
  stopped,
}

/// Runs one screen's refresh cycle on a bounded, lifecycle-aware schedule.
///
/// Guarantees:
///
/// * **One timer.** [start] twice schedules once; the timer is re-armed only
///   after a cycle completes, so ticks can never pile up.
/// * **No overlap.** A cycle never starts while another is in flight. A
///   [refreshNow] during a cycle is queued — at most one follow-up, however
///   many callers asked — and its future completes when that follow-up has.
/// * **Bounded retry.** Failures back off exponentially up to the ceiling;
///   a `401` or a permanent client error stops ticking altogether until an
///   explicit [refreshNow].
/// * **Visibility.** While any [PollPause] is active nothing is scheduled;
///   clearing the last one refreshes immediately.
/// * **Disposal.** After [dispose] no callback runs and nothing is published.
///
/// Time is read from `Timer` and the injected clock, so tests drive it with
/// fake time instead of real sleeps.
class PollingCoordinator {
  PollingCoordinator({
    required this._task,
    required this._interval,
    this._maxBackoff = const Duration(seconds: 60),
    AppActivity? activity,
    DateTime Function()? clock,
  }) : _clock = clock ?? AppClock.now {
    _debugLive++;
    if (activity != null) {
      if (!activity.isForeground.value) _pauses.add(PollPause.background);
      _activitySubscription = activity.isForeground.listen(
        (foreground) => foreground
            ? resume(PollPause.background)
            : pause(PollPause.background),
      );
    }
  }

  /// Coordinators constructed and not yet disposed, process-wide. Tests use
  /// it to prove that opening and leaving screens never accumulates pollers.
  @visibleForTesting
  static int get debugLiveCount => _debugLive;
  static int _debugLive = 0;

  final PollTask _task;

  /// The base interval for the next tick, or null when nothing on screen can
  /// change on its own. Re-evaluated after every cycle, which is what makes
  /// polling adaptive.
  final Duration? Function() _interval;

  final Duration _maxBackoff;
  final DateTime Function() _clock;

  final Rx<LiveStatus> status = LiveStatus.idle.obs;

  /// Only a successful, authoritative response moves this. A failed or
  /// cancelled cycle leaves it where it was.
  final Rxn<DateTime> lastSuccessfulUpdate = Rxn<DateTime>();

  final Rxn<DateTime> lastAttempt = Rxn<DateTime>();

  /// Consecutive transient failures. Reset by the first success.
  final RxInt failureCount = 0.obs;

  final Rxn<AppError> lastError = Rxn<AppError>();

  final Set<PollPause> _pauses = <PollPause>{};
  StreamSubscription<bool>? _activitySubscription;
  Timer? _timer;

  bool _started = false;
  bool _disposed = false;
  bool _inFlight = false;
  bool _rateLimited = false;

  /// [PollOutcome.unauthorized] or [PollOutcome.permanent] while ticking is
  /// suspended for want of operator action.
  PollOutcome? _block;

  PollTrigger? _queued;
  Completer<void>? _queuedDone;

  bool get isRunning => _started && !_disposed;
  bool get isPaused => _pauses.isNotEmpty;
  bool get isInFlight => _inFlight;
  bool get isDisposed => _disposed;

  /// Whether a tick is armed. At most one ever is.
  bool get hasScheduledTick => _timer?.isActive ?? false;

  /// The status ignoring an in-flight cycle — what the screen settles on
  /// once the current request returns. Used for accessibility announcements
  /// so a routine poll is never read aloud.
  LiveStatus get settledStatus => _compute(includeInFlight: false);

  /// The delay before the next tick: the adaptive base interval, stretched
  /// by backoff after failures. Null when nothing would be scheduled.
  Duration? get nextDelay {
    final base = _interval();
    if (base == null) return null;
    return PollBackoff.delay(
      base: base,
      failures: failureCount.value,
      max: _maxBackoff,
      rateLimited: _rateLimited,
    );
  }

  /// Begins ticking. Does not refresh by itself: the screen's first load is
  /// already a [refreshNow]. Idempotent.
  void start() {
    if (_disposed || _started) return;
    _started = true;
    _schedule();
    _publish();
  }

  /// Stops ticking. A cycle already in flight finishes, but schedules
  /// nothing afterwards.
  void stop() {
    if (!_started) return;
    _started = false;
    _cancelTimer();
    _publish();
  }

  void pause(PollPause reason) {
    if (_disposed || !_pauses.add(reason)) return;
    _cancelTimer();
    _publish();
  }

  /// Clears [reason]. When it was the last pause, refreshes immediately —
  /// unless ticking is suspended by a `401` or a permanent error, in which
  /// case only the operator can resume it.
  void resume(PollPause reason) {
    if (_disposed || !_pauses.remove(reason)) return;
    if (_started && _pauses.isEmpty && _block == null) {
      unawaited(refreshNow(trigger: PollTrigger.resume));
      return;
    }
    _publish();
  }

  /// Runs a cycle now, or — when one is already in flight — once right
  /// after it. Never two at a time.
  ///
  /// A manual refresh is the operator's explicit action, so it runs even
  /// while ticking is suspended, and a success resumes it.
  Future<void> refreshNow({PollTrigger trigger = PollTrigger.manual}) {
    if (_disposed) return Future<void>.value();
    if (_inFlight) {
      final queued = _queued;
      _queued = queued == null || trigger.index > queued.index
          ? trigger
          : queued;
      return (_queuedDone ??= Completer<void>()).future;
    }
    return _execute(trigger);
  }

  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _debugLive--;
    _started = false;
    _cancelTimer();
    unawaited(_activitySubscription?.cancel());
    _activitySubscription = null;
    _queued = null;
    _queuedDone?.complete();
    _queuedDone = null;
  }

  Future<void> _execute(PollTrigger trigger) async {
    _cancelTimer();
    _inFlight = true;
    lastAttempt.value = _clock().toUtc();
    _publish();

    AppError? error;
    try {
      error = await _task(trigger);
    } on AppError catch (caught) {
      error = caught;
    } on Object catch (caught) {
      // A defect in the task must not wedge the coordinator in "in flight"
      // forever. It surfaces as a failure and is retried with backoff.
      error = UnknownError(
        technicalDetail: 'Poll failed: ${caught.runtimeType}',
      );
    } finally {
      _inFlight = false;
    }
    if (_disposed) return;
    _apply(error);

    final queued = _queued;
    final done = _queuedDone;
    _queued = null;
    _queuedDone = null;
    // A queued background refresh must not re-send a credential the server
    // just rejected; only an explicit one may.
    if (queued != null && (_block == null || !queued.isBackground)) {
      await _execute(queued);
      done?.complete();
      return;
    }
    done?.complete();

    _schedule();
    _publish();
  }

  void _apply(AppError? error) {
    final outcome = PollOutcome.of(error);
    switch (outcome) {
      case PollOutcome.success:
        failureCount.value = 0;
        _rateLimited = false;
        _block = null;
        lastError.value = null;
        lastSuccessfulUpdate.value = _clock().toUtc();
      case PollOutcome.cancelled:
        break;
      case PollOutcome.transient || PollOutcome.rateLimited:
        failureCount.value++;
        _rateLimited = outcome == PollOutcome.rateLimited;
        _block = null;
        lastError.value = error;
      case PollOutcome.unauthorized || PollOutcome.permanent:
        _block = outcome;
        lastError.value = error;
    }
  }

  void _schedule() {
    _cancelTimer();
    if (!_started || _disposed || _inFlight || _pauses.isNotEmpty) return;
    if (_block != null) return;
    final delay = nextDelay;
    if (delay == null) return;
    _timer = Timer(delay, _onTick);
  }

  void _onTick() {
    _timer = null;
    if (_disposed || _inFlight) return;
    unawaited(_execute(PollTrigger.tick));
  }

  void _cancelTimer() {
    _timer?.cancel();
    _timer = null;
  }

  void _publish() {
    if (_disposed) return;
    status.value = _compute(includeInFlight: true);
  }

  LiveStatus _compute({required bool includeInFlight}) {
    if (includeInFlight && _inFlight) return LiveStatus.refreshing;
    if (_block == PollOutcome.unauthorized) return LiveStatus.accessRequired;
    if (_block == PollOutcome.permanent) return LiveStatus.halted;
    if (_started && _pauses.isNotEmpty) return LiveStatus.paused;
    if (failureCount.value > 0) {
      final error = lastError.value;
      return error is NetworkError || error is TimeoutError
          ? LiveStatus.offline
          : LiveStatus.retrying;
    }
    if (lastSuccessfulUpdate.value == null) return LiveStatus.idle;
    if (_started && _interval() == null) return LiveStatus.stopped;
    return LiveStatus.live;
  }
}
