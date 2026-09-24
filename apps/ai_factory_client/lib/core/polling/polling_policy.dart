import 'dart:math' as math;

import '../errors/app_error.dart';

/// Every polling interval the client uses, in one place.
///
/// The backend has no push transport (gap `G-REALTIME`), so screens that show
/// changing work re-read it on a timer. These are the defaults; a controller
/// takes a [PollIntervals] in its constructor, so a deployment or a test can
/// substitute its own without touching a widget.
///
/// Work items and agent runs are not listed separately from Project Detail:
/// they are refreshed in the same coordinated cycle as the project itself,
/// and the cycle picks [agentRuns] when a run is executing.
class PollIntervals {
  const PollIntervals({
    this.dashboard = const Duration(seconds: 10),
    this.projects = const Duration(seconds: 10),
    this.projectDetail = const Duration(seconds: 5),
    this.agentRuns = const Duration(seconds: 3),
    this.humanReview = const Duration(seconds: 5),
    this.diagnostics = const Duration(seconds: 10),
    this.operations = const Duration(seconds: 15),
    this.idle = const Duration(seconds: 30),
    this.maxBackoff = const Duration(seconds: 60),
  });

  static const PollIntervals defaults = PollIntervals();

  final Duration dashboard;
  final Duration projects;

  /// A project the Factory is actively advancing.
  final Duration projectDetail;

  /// Project Detail while at least one agent run is executing.
  final Duration agentRuns;

  /// A project waiting for approval — polled so that another operator's
  /// approval is noticed.
  final Duration humanReview;

  final Duration diagnostics;
  final Duration operations;

  /// Nothing visible is advancing on its own (`Created`, `Paused`, or a list
  /// with no active project).
  final Duration idle;

  /// Upper bound for [PollBackoff] after repeated failures.
  final Duration maxBackoff;
}

/// Why a refresh is running. Background triggers refresh silently — the data
/// on screen is replaced when the response arrives, with no progress line;
/// a manual one shows the screen's own refresh indicator.
enum PollTrigger {
  /// The timer fired.
  tick,

  /// The screen became visible again, or the application returned to the
  /// foreground.
  resume,

  /// The operator asked, the screen opened, or a mutation just succeeded.
  manual;

  bool get isBackground => this != manual;
}

/// What one refresh cycle means for the next one.
enum PollOutcome {
  success,

  /// Superseded or aborted. Changes nothing: not a success, not a failure.
  cancelled,

  /// Network, timeout, 5xx, an unavailable dependency. Retried with backoff.
  transient,

  /// `429`. Retried at the backoff ceiling straight away.
  rateLimited,

  /// `401`. Polling stops until the operator supplies a credential — the same
  /// invalid key is never sent again on a timer.
  unauthorized,

  /// `400` / `403` / `404` / `409` / `422`. Will not change by itself, so
  /// polling stops until the operator acts.
  permanent;

  bool get blocksPolling => this == unauthorized || this == permanent;

  static PollOutcome of(AppError? error) => switch (error) {
    null => success,
    RequestCancelledError() => cancelled,
    AuthenticationError() => unauthorized,
    RateLimitError() => rateLimited,
    NotFoundError() ||
    ValidationError() ||
    ForbiddenError() ||
    ConflictError() => permanent,
    // NetworkError, TimeoutError, ServerError, ProviderFailure, UnknownError.
    _ => transient,
  };

  /// The error that should drive scheduling after a cycle that read several
  /// sources at once.
  ///
  /// The [primary] source is the one the screen cannot exist without (the
  /// project on Project Detail): a permanent failure there — a deleted
  /// project — stops polling. A permanent failure in a [secondary] section is
  /// shown by that section and does not stop the rest of the screen from
  /// refreshing. Otherwise the most severe error wins: `401`, then `429`,
  /// then a transient failure.
  static AppError? decisive({
    AppError? primary,
    Iterable<AppError?> secondary = const <AppError?>[],
  }) {
    final primaryOutcome = of(primary);
    if (primaryOutcome == permanent) return primary;

    final all = <AppError?>[primary, ...secondary];
    for (final wanted in const [unauthorized, rateLimited, transient]) {
      for (final error in all) {
        if (of(error) == wanted) return error;
      }
    }
    return primaryOutcome == cancelled ? primary : null;
  }
}

/// Bounded exponential backoff: `base`, `2 × base`, `4 × base`, … capped at
/// [PollIntervals.maxBackoff]. A `429` goes to the cap immediately.
class PollBackoff {
  const PollBackoff._();

  static Duration delay({
    required Duration base,
    required int failures,
    required Duration max,
    bool rateLimited = false,
  }) {
    if (rateLimited) return max;
    if (failures <= 0) return base < max ? base : max;
    // The exponent is clamped so a long outage cannot overflow the multiply.
    final factor = math.pow(2, math.min(failures, 16)).toInt();
    final scaled = base * factor;
    return scaled < max ? scaled : max;
  }
}
