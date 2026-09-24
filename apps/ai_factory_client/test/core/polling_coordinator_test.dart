import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/polling/app_activity.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

/// [PollingCoordinator] under fake time. No test here sleeps: every interval,
/// backoff step and lifecycle transition is driven by `FakeAsync.elapse`.
void main() {
  const base = Duration(seconds: 5);
  const max = Duration(seconds: 60);
  final epoch = DateTime.utc(2026, 9, 23, 10);

  /// A scripted poll task: counts calls, answers with [next], and can be held
  /// open by [gate] to simulate a slow request.
  late _ScriptedTask task;

  setUp(() => task = _ScriptedTask());

  PollingCoordinator build(
    FakeAsync async, {
    Duration? Function()? interval,
    AppActivity? activity,
  }) => PollingCoordinator(
    task: task.call,
    interval: interval ?? () => base,
    maxBackoff: max,
    activity: activity,
    clock: () => epoch.add(async.elapsed),
  );

  group('lifecycle', () {
    test('start does not poll by itself; the first tick is one interval '
        'later', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.flushMicrotasks();
        expect(task.calls, 0);

        async.elapse(base - const Duration(milliseconds: 1));
        expect(task.calls, 0);
        async.elapse(const Duration(milliseconds: 1));
        expect(task.calls, 1);

        poller.dispose();
      });
    });

    test('ticks at the normal interval while nothing fails', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.elapse(base * 4);
        expect(task.calls, 4);
        expect(poller.status.value, LiveStatus.live);
        poller.dispose();
      });
    });

    test('calling start twice never creates a second timer', () {
      fakeAsync((async) {
        final poller = build(async)
          ..start()
          ..start();
        expect(async.pendingTimers.length, 1);

        async.elapse(base * 3);
        expect(task.calls, 3, reason: 'one timer, one call per interval');
        poller.dispose();
      });
    });

    test('stop prevents every future callback', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.elapse(base);
        expect(task.calls, 1);

        poller.stop();
        async.elapse(base * 10);
        expect(task.calls, 1);
        expect(async.pendingTimers, isEmpty);
        poller.dispose();
      });
    });

    test('a cycle in flight when stop is called schedules nothing after', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.gate = Completer<void>();
        async.elapse(base);
        expect(poller.isInFlight, isTrue);

        poller.stop();
        task.gate!.complete();
        async.elapse(base * 5);
        expect(task.calls, 1);
        expect(async.pendingTimers, isEmpty);
        poller.dispose();
      });
    });

    test('dispose releases the timer and the lifecycle subscription', () {
      fakeAsync((async) {
        final activity = AppActivity();
        final poller = build(async, activity: activity)..start();
        poller.dispose();
        async.flushMicrotasks();

        expect(async.pendingTimers, isEmpty);
        activity.isForeground.value = false;
        activity.isForeground.value = true;
        async.elapse(base * 5);
        expect(task.calls, 0, reason: 'nothing runs after disposal');
        expect(poller.isDisposed, isTrue);
      });
    });

    test('a refresh after dispose is a no-op', () {
      fakeAsync((async) {
        final poller = build(async)..dispose();
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 0);
      });
    });
  });

  group('refreshNow', () {
    test('runs immediately without waiting for the interval', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 1);
        expect(task.triggers.single, PollTrigger.manual);
        poller.dispose();
      });
    });

    test('re-arms the timer from the refresh, not from the old tick', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.elapse(const Duration(seconds: 3));
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 1);

        // The old tick at t=5 s was cancelled; the next is at t=3+5 s.
        async.elapse(const Duration(seconds: 4));
        expect(task.calls, 1);
        async.elapse(const Duration(seconds: 1));
        expect(task.calls, 2);
        poller.dispose();
      });
    });

    test('works on a coordinator that was never started', () {
      fakeAsync((async) {
        final poller = build(async);
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 1);
        expect(async.pendingTimers, isEmpty, reason: 'not started: no tick');
        poller.dispose();
      });
    });
  });

  group('no overlap', () {
    test('a slow request is never overlapped by the next tick', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.gate = Completer<void>();
        async.elapse(base);
        expect(task.calls, 1);

        // Far past several intervals: still only the one request.
        async.elapse(base * 6);
        expect(task.calls, 1);
        expect(task.maxConcurrent, 1);

        task.gate!.complete();
        async.flushMicrotasks();
        async.elapse(base);
        expect(task.calls, 2);
        expect(task.maxConcurrent, 1);
        poller.dispose();
      });
    });

    test('refreshNow during a request queues exactly one follow-up', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.gate = Completer<void>();
        unawaited(poller.refreshNow());
        async.flushMicrotasks();

        var completed = 0;
        for (var i = 0; i < 5; i++) {
          unawaited(poller.refreshNow().then((_) => completed++));
        }
        async.flushMicrotasks();
        expect(task.calls, 1, reason: 'nothing runs concurrently');

        final gate = task.gate!;
        task.gate = null;
        gate.complete();
        async.flushMicrotasks();

        expect(task.calls, 2, reason: 'five requests collapse into one');
        expect(task.maxConcurrent, 1);
        expect(completed, 5, reason: 'every caller learns it has run');
        poller.dispose();
      });
    });

    test('a queued manual refresh wins over a queued background one', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.gate = Completer<void>();
        unawaited(poller.refreshNow(trigger: PollTrigger.tick));
        async.flushMicrotasks();
        unawaited(poller.refreshNow(trigger: PollTrigger.resume));
        unawaited(poller.refreshNow());

        final gate = task.gate!;
        task.gate = null;
        gate.complete();
        async.flushMicrotasks();

        expect(task.triggers, [PollTrigger.tick, PollTrigger.manual]);
        poller.dispose();
      });
    });
  });

  group('adaptive interval', () {
    test('the interval is re-evaluated after every cycle', () {
      fakeAsync((async) {
        var current = const Duration(seconds: 3);
        final poller = build(async, interval: () => current)..start();

        async.elapse(const Duration(seconds: 3));
        expect(task.calls, 1);

        // The screen settled down: the next tick uses the longer interval.
        current = const Duration(seconds: 30);
        async.elapse(const Duration(seconds: 3));
        expect(task.calls, 2, reason: 'already armed at the old interval');
        async.elapse(const Duration(seconds: 29));
        expect(task.calls, 2);
        async.elapse(const Duration(seconds: 1));
        expect(task.calls, 3);
        poller.dispose();
      });
    });

    test('a null interval stops scheduling and reports stopped', () {
      fakeAsync((async) {
        Duration? current = base;
        final poller = build(async, interval: () => current)..start();
        async.elapse(base);
        expect(task.calls, 1);

        current = null;
        async.elapse(base);
        expect(task.calls, 2);
        expect(async.pendingTimers, isEmpty);
        expect(poller.status.value, LiveStatus.stopped);

        async.elapse(base * 20);
        expect(task.calls, 2, reason: 'a terminal screen is not polled');

        // A manual refresh still works, and resumes when there is change.
        current = base;
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 3);
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });
  });

  group('backoff', () {
    test('doubles after each transient failure up to the ceiling', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const NetworkError();

        final expected = <Duration>[
          const Duration(seconds: 5), // first tick
          const Duration(seconds: 10), // failure 1
          const Duration(seconds: 20), // failure 2
          const Duration(seconds: 40), // failure 3
          const Duration(seconds: 60), // failure 4, capped
          const Duration(seconds: 60), // still capped
        ];
        for (var i = 0; i < expected.length; i++) {
          async.elapse(expected[i] - const Duration(milliseconds: 1));
          expect(task.calls, i, reason: 'tick ${i + 1} must not fire early');
          async.elapse(const Duration(milliseconds: 1));
          expect(task.calls, i + 1);
        }
        expect(poller.failureCount.value, expected.length);
        poller.dispose();
      });
    });

    test('resets to the normal interval after one success', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const ServerError();
        async.elapse(base); // failure 1
        async.elapse(base * 2); // failure 2
        expect(poller.nextDelay, base * 4);

        task.next = null;
        async.elapse(base * 4); // success
        expect(poller.failureCount.value, 0);
        expect(poller.nextDelay, base);
        expect(poller.status.value, LiveStatus.live);
        poller.dispose();
      });
    });

    test('a 429 backs off to the ceiling immediately', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const RateLimitError();
        async.elapse(base);
        expect(poller.nextDelay, max);
        poller.dispose();
      });
    });

    test('PollBackoff never exceeds the ceiling and never overflows', () {
      expect(PollBackoff.delay(base: base, failures: 0, max: max), base);
      expect(PollBackoff.delay(base: base, failures: 1, max: max), base * 2);
      expect(PollBackoff.delay(base: base, failures: 1000, max: max), max);
      expect(
        PollBackoff.delay(
          base: const Duration(minutes: 5),
          failures: 0,
          max: max,
        ),
        max,
      );
    });
  });

  group('error classes', () {
    test('a network failure keeps polling and reports offline', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const NetworkError();
        async.elapse(base);
        expect(poller.status.value, LiveStatus.offline);
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });

    test('a 5xx keeps polling and reports retrying', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const ServerError(statusCode: 500);
        async.elapse(base);
        expect(poller.status.value, LiveStatus.retrying);
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });

    test('a 404 halts polling instead of looping', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const NotFoundError();
        async.elapse(base);
        expect(poller.status.value, LiveStatus.halted);
        expect(async.pendingTimers, isEmpty);

        async.elapse(const Duration(minutes: 10));
        expect(task.calls, 1, reason: 'a deleted project is not re-polled');
        poller.dispose();
      });
    });

    test('400 and 422 are permanent too', () {
      expect(
        PollOutcome.of(const ValidationError(statusCode: 422)),
        PollOutcome.permanent,
      );
      expect(
        PollOutcome.of(const ValidationError(statusCode: 400)),
        PollOutcome.permanent,
      );
    });

    test('a 401 pauses polling until the operator acts', () {
      fakeAsync((async) {
        final activity = AppActivity();
        final poller = build(async, activity: activity)..start();
        task.next = const AuthenticationError();
        async.elapse(base);
        expect(poller.status.value, LiveStatus.accessRequired);
        expect(async.pendingTimers, isEmpty);

        // Neither time nor an app resume re-sends the rejected credential.
        async.elapse(const Duration(minutes: 10));
        activity.isForeground.value = false;
        async.flushMicrotasks();
        activity.isForeground.value = true;
        async.flushMicrotasks();
        expect(task.calls, 1);

        // The operator entered a key: an explicit refresh resumes polling.
        task.next = null;
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        expect(task.calls, 2);
        expect(poller.status.value, LiveStatus.live);
        async.elapse(base);
        expect(task.calls, 3);
        poller.dispose();
      });
    });

    test('a queued background refresh is dropped after a 401', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task
          ..gate = Completer<void>()
          ..next = const AuthenticationError();
        unawaited(poller.refreshNow());
        async.flushMicrotasks();
        unawaited(poller.refreshNow(trigger: PollTrigger.resume));

        task.gate!.complete();
        async.flushMicrotasks();
        expect(task.calls, 1);
        poller.dispose();
      });
    });

    test('a cancelled cycle is neither a success nor a failure', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.next = const RequestCancelledError();
        async.elapse(base);
        expect(poller.failureCount.value, 0);
        expect(poller.lastSuccessfulUpdate.value, isNull);
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });

    test('a task that throws a non-AppError does not wedge the loop', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        task.throwing = StateError('defect');
        async.elapse(base);
        expect(poller.isInFlight, isFalse);
        expect(poller.lastError.value, isA<UnknownError>());
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });
  });

  group('last updated', () {
    test('moves only on success', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.elapse(base);
        final first = poller.lastSuccessfulUpdate.value;
        expect(first, epoch.add(base));

        task.next = const NetworkError();
        async.elapse(base * 2);
        expect(poller.lastSuccessfulUpdate.value, first);
        expect(poller.lastAttempt.value, epoch.add(base * 2));

        // The retry is due at t=20 s (backoff 2 × 5 s after the t=10 s
        // failure); stop just after it.
        task.next = null;
        async.elapse(base + const Duration(seconds: 1));
        expect(poller.lastSuccessfulUpdate.value, epoch.add(base * 4));
        poller.dispose();
      });
    });
  });

  group('network recovery', () {
    test('online, offline with backoff, then recovery without restart', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        async.elapse(base);
        expect(poller.status.value, LiveStatus.live);

        task.next = const NetworkError();
        async.elapse(base);
        async.elapse(base * 2);
        expect(poller.status.value, LiveStatus.offline);
        expect(poller.failureCount.value, 2);
        expect(poller.nextDelay, base * 4);

        task.next = null;
        async.elapse(base * 4);
        expect(poller.status.value, LiveStatus.live);
        expect(poller.failureCount.value, 0);
        expect(poller.nextDelay, base);
        poller.dispose();
      });
    });
  });

  group('pause and resume', () {
    test('pause stops ticking; resume refreshes immediately', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        poller.pause(PollPause.hidden);
        expect(poller.status.value, LiveStatus.paused);
        async.elapse(base * 5);
        expect(task.calls, 0);

        poller.resume(PollPause.hidden);
        async.flushMicrotasks();
        expect(task.calls, 1);
        expect(task.triggers.single, PollTrigger.resume);
        expect(poller.hasScheduledTick, isTrue);
        poller.dispose();
      });
    });

    test('resuming a reason that was never paused does nothing', () {
      fakeAsync((async) {
        final poller = build(async)..start();
        poller.resume(PollPause.hidden);
        async.flushMicrotasks();
        expect(task.calls, 0);
        poller.dispose();
      });
    });

    test('both pauses must clear before polling resumes', () {
      fakeAsync((async) {
        final poller = build(async)
          ..start()
          ..pause(PollPause.hidden)
          ..pause(PollPause.background)
          ..resume(PollPause.background);
        async.flushMicrotasks();
        expect(task.calls, 0, reason: 'still covered by another page');

        poller.resume(PollPause.hidden);
        async.flushMicrotasks();
        expect(task.calls, 1);
        poller.dispose();
      });
    });

    test('app lifecycle: background pauses, foreground refreshes at once', () {
      fakeAsync((async) {
        final activity = AppActivity();
        final poller = build(async, activity: activity)..start();

        activity.isForeground.value = false;
        async.flushMicrotasks();
        expect(poller.isPaused, isTrue);
        async.elapse(base * 5);
        expect(task.calls, 0);

        activity.isForeground.value = true;
        async.flushMicrotasks();
        expect(task.calls, 1);
        expect(poller.isPaused, isFalse);
        poller.dispose();
      });
    });

    test('a coordinator created in the background starts paused', () {
      fakeAsync((async) {
        final activity = AppActivity()..isForeground.value = false;
        final poller = build(async, activity: activity)..start();
        async.elapse(base * 3);
        expect(task.calls, 0);
        poller.dispose();
      });
    });
  });

  group('AppActivity', () {
    test('only hidden, paused and detached count as background', () {
      final activity = AppActivity();
      final expected = <AppLifecycleState, bool>{
        AppLifecycleState.resumed: true,
        AppLifecycleState.inactive: true,
        AppLifecycleState.hidden: false,
        AppLifecycleState.paused: false,
        AppLifecycleState.detached: false,
      };
      for (final entry in expected.entries) {
        activity.onLifecycleState(entry.key);
        expect(
          activity.isForeground.value,
          entry.value,
          reason: '${entry.key}',
        );
      }
    });
  });

  group('PollOutcome.decisive', () {
    test('a permanent failure of the primary source wins', () {
      const notFound = NotFoundError();
      expect(
        PollOutcome.decisive(
          primary: notFound,
          secondary: const [NetworkError()],
        ),
        notFound,
      );
    });

    test('a permanent failure of a secondary section does not halt', () {
      expect(
        PollOutcome.decisive(secondary: const [NotFoundError(), null]),
        isNull,
      );
    });

    test('401 outranks 429, which outranks a transient failure', () {
      const auth = AuthenticationError();
      const limited = RateLimitError();
      expect(
        PollOutcome.decisive(secondary: const [NetworkError(), limited, auth]),
        auth,
      );
      expect(
        PollOutcome.decisive(secondary: const [NetworkError(), limited]),
        limited,
      );
    });

    test('a cancelled primary is reported as cancelled', () {
      expect(
        PollOutcome.decisive(
          primary: const RequestCancelledError(),
          secondary: const [null],
        ),
        isA<RequestCancelledError>(),
      );
    });
  });
}

class _ScriptedTask {
  int calls = 0;
  int _running = 0;
  int maxConcurrent = 0;
  final List<PollTrigger> triggers = <PollTrigger>[];

  /// What the next cycle reports. Null is success.
  AppError? next;

  /// Thrown instead of returning, to simulate a defect in a task.
  Object? throwing;

  /// Holds the cycle open until completed.
  Completer<void>? gate;

  Future<AppError?> call(PollTrigger trigger) async {
    calls++;
    triggers.add(trigger);
    _running++;
    if (_running > maxConcurrent) maxConcurrent = _running;
    try {
      final hold = gate;
      if (hold != null) await hold.future;
      final defect = throwing;
      if (defect != null) throw defect;
      return next;
    } finally {
      _running--;
    }
  }
}
