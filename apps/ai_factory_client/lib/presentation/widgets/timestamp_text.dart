import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/localization/relative_time.dart';

/// An API timestamp, rendered once and correctly.
///
/// "2 minutes ago" is what an operator reads at a glance; the absolute local
/// time is what they need when correlating with a server log, so it sits in
/// the tooltip and in the semantics label rather than in a second column.
///
/// Conversion lives in `RelativeTime` / `Formatters` and nowhere else: widgets
/// never subtract `DateTime`s, never call `toLocal()` and never re-parse an
/// API string. The value arriving here is already UTC — the DTO converters
/// guarantee it, including on the read paths where the backend omits the `Z`.
class TimestampText extends StatelessWidget {
  const TimestampText({required this.timestamp, this.style, super.key})
    : fallback = null;

  /// Renders [fallback] instead when [timestamp] is null, which is how "not
  /// started" and "never" are shown without inventing a time.
  const TimestampText.orText({
    required this.timestamp,
    required this.fallback,
    this.style,
    super.key,
  });

  final DateTime? timestamp;
  final TextStyle? style;

  /// Rendered when [timestamp] is null.
  final String? fallback;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final effective = style ?? theme.textTheme.bodySmall;
    final value = timestamp;

    if (value == null) {
      return Text(fallback ?? 'common.notAvailable'.tr(), style: effective);
    }

    final locale = context.locale.toString();
    final relative = RelativeTime.format(value, locale);
    final absolute = RelativeTime.absolute(value, locale);

    return Tooltip(
      message: absolute,
      child: Semantics(
        label: '$relative, $absolute',
        excludeSemantics: true,
        child: Text(relative, style: effective),
      ),
    );
  }
}
