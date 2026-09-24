import 'package:ai_factory_client/core/localization/relative_time.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/test_app.dart';

/// Relative time is a presentation concern, so it is tested through a real
/// localization scope rather than against hard-coded strings.
void main() {
  setUpAll(initializeTestLocalization);

  setUp(registerTestServices);

  /// Renders [RelativeTime.format] inside a localized subtree and returns
  /// what the operator would actually read.
  Future<String> render(
    WidgetTester tester,
    DateTime timestamp, {
    required DateTime now,
    Locale locale = const Locale('en'),
  }) async {
    late String rendered;
    await pumpScreen(
      tester,
      startLocale: locale,
      home: Builder(
        builder: (context) {
          rendered = RelativeTime.format(
            timestamp,
            context.locale.toString(),
            now: now,
          );
          return Scaffold(body: Text(rendered));
        },
      ),
    );
    return rendered;
  }

  final now = DateTime.utc(2026, 9, 19, 12);

  testWidgets('anything under a minute reads as just now', (tester) async {
    expect(
      await render(tester, now.subtract(const Duration(seconds: 30)), now: now),
      'Just now',
    );
  });

  testWidgets('a clock skew into the future also reads as just now', (
    tester,
  ) async {
    // Client and server clocks differ; "in 3 seconds" would be noise.
    expect(
      await render(tester, now.add(const Duration(seconds: 3)), now: now),
      'Just now',
    );
  });

  testWidgets('minutes, hours and days each have their own form', (
    tester,
  ) async {
    expect(
      await render(tester, now.subtract(const Duration(minutes: 2)), now: now),
      '2 min ago',
    );
    expect(
      await render(tester, now.subtract(const Duration(hours: 5)), now: now),
      '5 h ago',
    );
    expect(
      await render(tester, now.subtract(const Duration(days: 3)), now: now),
      '3 d ago',
    );
  });

  testWidgets('beyond a week it falls back to an absolute date', (
    tester,
  ) async {
    final rendered = await render(
      tester,
      now.subtract(const Duration(days: 30)),
      now: now,
    );

    expect(rendered, contains('2026'));
    expect(rendered, isNot(contains('ago')));
  });

  testWidgets('German uses German wording', (tester) async {
    expect(
      await render(
        tester,
        now.subtract(const Duration(minutes: 2)),
        now: now,
        locale: const Locale('de'),
      ),
      'vor 2 Min.',
    );
    expect(
      await render(
        tester,
        now.subtract(const Duration(hours: 5)),
        now: now,
        locale: const Locale('de'),
      ),
      'vor 5 Std.',
    );
  });

  testWidgets('a naive timestamp is treated as UTC, not as local time', (
    tester,
  ) async {
    // The backend drops the `Z` on read paths; the data layer restores UTC.
    // This asserts the presentation layer does not undo that by comparing a
    // UTC instant against a local one.
    final naive = DateTime(2026, 9, 19, 12).toUtc();
    expect(await render(tester, naive, now: naive), 'Just now');
  });
}
