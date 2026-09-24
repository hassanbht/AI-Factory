import 'package:flutter/foundation.dart';

/// The application's one source of "now" and of local-time conversion.
///
/// Production reads the system clock and the system time zone. Golden and
/// accessibility tests pin both, so a relative label ("2 hours ago"), a
/// "Last updated" time or an absolute date renders identically on every
/// machine and in every time zone — otherwise a screenshot would drift with
/// the wall clock and with the CI runner's zone.
class AppClock {
  const AppClock._();

  static DateTime Function() _now = DateTime.now;
  static DateTime Function(DateTime utc) _toLocal = _systemLocal;

  static DateTime now() => _now();

  /// Converts an API (UTC) timestamp for display.
  static DateTime toLocal(DateTime utc) => _toLocal(utc);

  /// Pins the clock and the display zone for a test; [reset] restores both.
  @visibleForTesting
  static void pin({
    required DateTime now,
    DateTime Function(DateTime utc)? toLocal,
  }) {
    _now = () => now;
    _toLocal = toLocal ?? _systemLocal;
  }

  /// Drives "now" from a test's fake time, so timestamps advance with it.
  @visibleForTesting
  static void follow(DateTime Function() now) => _now = now;

  @visibleForTesting
  static void reset() {
    _now = DateTime.now;
    _toLocal = _systemLocal;
  }

  static DateTime _systemLocal(DateTime utc) => utc.isUtc ? utc.toLocal() : utc;
}
