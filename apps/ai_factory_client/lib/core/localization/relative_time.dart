import 'package:easy_localization/easy_localization.dart';

import '../utils/formatters.dart';

/// "2 minutes ago" / "vor 2 Minuten".
///
/// The one place relative time is computed. Screens never subtract
/// `DateTime`s themselves, so the rounding rules, the UTC handling and the
/// switch to an absolute date stay identical everywhere.
///
/// API timestamps are UTC (`UtcDateTimeConverter` guarantees it, including
/// for the read paths where the backend drops the `Z`). The comparison is
/// done in UTC and only the absolute fallback is converted to local time.
class RelativeTime {
  const RelativeTime._();

  /// Beyond this, a relative label stops being useful and an absolute date is
  /// clearer.
  static const Duration absoluteThreshold = Duration(days: 7);

  static String format(DateTime timestamp, String locale, {DateTime? now}) {
    final reference = (now ?? DateTime.now()).toUtc();
    final value = timestamp.isUtc ? timestamp : timestamp.toUtc();
    final elapsed = reference.difference(value);

    // A clock skew between client and server can put a timestamp slightly in
    // the future. "In 3 seconds" would be noise, so it reads as just now.
    if (elapsed.isNegative || elapsed.inSeconds < 60) {
      return 'time.justNow'.tr();
    }
    if (elapsed.inMinutes < 60) {
      return 'time.minutesAgo'.tr(args: ['${elapsed.inMinutes}']);
    }
    if (elapsed.inHours < 24) {
      return 'time.hoursAgo'.tr(args: ['${elapsed.inHours}']);
    }
    if (elapsed < absoluteThreshold) {
      return 'time.daysAgo'.tr(args: ['${elapsed.inDays}']);
    }
    return Formatters.date(value, locale);
  }

  /// The absolute timestamp, for a tooltip or a screen-reader label beside
  /// the relative one.
  static String absolute(DateTime timestamp, String locale) =>
      Formatters.dateTime(timestamp, locale);
}
