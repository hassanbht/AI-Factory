import 'package:intl/intl.dart';

/// Locale-aware formatting helpers.
///
/// Every date, duration and number in the UI goes through one of these, so
/// German renders `19.09.2026, 14:03` and `1.234,5` without any screen having
/// to know about it.
class Formatters {
  const Formatters._();

  /// Absolute date and time in the viewer's local time zone. API timestamps
  /// are UTC and are converted here, never displayed raw.
  static String dateTime(DateTime utc, String locale) {
    final local = utc.isUtc ? utc.toLocal() : utc;
    return DateFormat.yMMMd(locale).add_Hm().format(local);
  }

  static String date(DateTime utc, String locale) {
    final local = utc.isUtc ? utc.toLocal() : utc;
    return DateFormat.yMMMd(locale).format(local);
  }

  static String number(num value, String locale) =>
      NumberFormat.decimalPattern(locale).format(value);

  /// Compact duration: `1h 04m`, `12.3s`, `840ms`. Unit letters are
  /// locale-independent on purpose — they are symbols, not words, and stay
  /// legible in both supported languages.
  static String duration(Duration duration, String locale) {
    if (duration.inMilliseconds < 1000) {
      return '${duration.inMilliseconds}ms';
    }
    if (duration.inSeconds < 60) {
      final seconds = duration.inMilliseconds / 1000;
      return '${NumberFormat('0.#', locale).format(seconds)}s';
    }
    if (duration.inMinutes < 60) {
      final seconds = duration.inSeconds
          .remainder(60)
          .toString()
          .padLeft(2, '0');
      return '${duration.inMinutes}m ${seconds}s';
    }
    final minutes = duration.inMinutes.remainder(60).toString().padLeft(2, '0');
    return '${duration.inHours}h ${minutes}m';
  }

  static String durationMs(int milliseconds, String locale) =>
      duration(Duration(milliseconds: milliseconds), locale);
}
