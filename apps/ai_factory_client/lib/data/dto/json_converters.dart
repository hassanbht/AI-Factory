import 'package:json_annotation/json_annotation.dart';

/// Parses an AI Factory timestamp as UTC, with or without a `Z` suffix.
///
/// The backend is inconsistent about this, verified against the running
/// `v1.0.0` API:
///
///     POST /api/projects -> "createdAtUtc":"2026-09-19T19:02:11.4453985Z"
///     GET  /api/projects -> "createdAtUtc":"2026-09-19T19:02:11.4453985"
///
/// A value written in this request carries `Z` (the in-memory `DateTime` has
/// `Kind=Utc`), while the same value read back from SQLite does not (SQLite
/// restores it as `Kind=Unspecified`). Every field is documented as UTC and
/// named `...AtUtc`, so the missing suffix is a serialization artefact, not a
/// change of meaning.
///
/// `DateTime.parse` would treat the suffix-less form as **local** time and
/// silently shift every read-back timestamp by the operator's UTC offset — an
/// hour or two of invisible error on durations, "last updated" and the
/// lifecycle timeline. This converter always produces UTC.
class UtcDateTimeConverter implements JsonConverter<DateTime, String> {
  const UtcDateTimeConverter();

  static DateTime parse(String value) {
    final parsed = DateTime.parse(value);
    if (parsed.isUtc) return parsed;
    // No offset in the payload: re-read the same wall-clock reading as UTC.
    return DateTime.utc(
      parsed.year,
      parsed.month,
      parsed.day,
      parsed.hour,
      parsed.minute,
      parsed.second,
      parsed.millisecond,
      parsed.microsecond,
    );
  }

  static DateTime? tryParse(Object? value) {
    if (value is! String || value.isEmpty) return null;
    try {
      return parse(value);
    } on FormatException {
      return null;
    }
  }

  @override
  DateTime fromJson(String json) => parse(json);

  @override
  String toJson(DateTime object) => object.toUtc().toIso8601String();
}

/// The nullable counterpart of [UtcDateTimeConverter].
class NullableUtcDateTimeConverter
    implements JsonConverter<DateTime?, String?> {
  const NullableUtcDateTimeConverter();

  @override
  DateTime? fromJson(String? json) =>
      (json == null || json.isEmpty) ? null : UtcDateTimeConverter.parse(json);

  @override
  String? toJson(DateTime? object) => object?.toUtc().toIso8601String();
}

/// .NET serializes a `TimeSpan` as `"[-][d.]hh:mm:ss[.fffffff]"`, for example
/// `"00:00:00.0002757"` on `/api/operations/deployment`. Dart has no parser
/// for that shape, so uptime would otherwise be an unusable string.
class TimeSpanConverter implements JsonConverter<Duration?, String?> {
  const TimeSpanConverter();

  static Duration? tryParse(Object? value) {
    if (value is! String || value.isEmpty) return null;

    var text = value;
    var negative = false;
    if (text.startsWith('-')) {
      negative = true;
      text = text.substring(1);
    }

    var days = 0;
    // A leading "d." is only a day component when what follows still holds a
    // full hh:mm:ss — otherwise the dot belongs to the fractional seconds.
    final firstDot = text.indexOf('.');
    final firstColon = text.indexOf(':');
    if (firstDot >= 0 && firstColon >= 0 && firstDot < firstColon) {
      days = int.tryParse(text.substring(0, firstDot)) ?? 0;
      text = text.substring(firstDot + 1);
    }

    final parts = text.split(':');
    if (parts.length != 3) return null;

    final hours = int.tryParse(parts[0]);
    final minutes = int.tryParse(parts[1]);
    if (hours == null || minutes == null) return null;

    final secondsParts = parts[2].split('.');
    final seconds = int.tryParse(secondsParts[0]);
    if (seconds == null) return null;

    var microseconds = 0;
    if (secondsParts.length > 1) {
      // .NET writes up to 7 digits (100 ns ticks); Dart resolves microseconds.
      final fraction = secondsParts[1].padRight(6, '0').substring(0, 6);
      microseconds = int.tryParse(fraction) ?? 0;
    }

    final duration = Duration(
      days: days,
      hours: hours,
      minutes: minutes,
      seconds: seconds,
      microseconds: microseconds,
    );

    return negative ? -duration : duration;
  }

  @override
  Duration? fromJson(String? json) => tryParse(json);

  @override
  String? toJson(Duration? object) => object?.toString();
}
