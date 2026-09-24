import 'package:ai_factory_client/core/utils/app_clock.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../golden/golden_support.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// Every real screen at 100 %, 125 %, 150 % and 200 % text, in English and
/// German, at the compact and expanded breakpoints.
///
/// A layout overflow is reported by the framework as a test failure, so a
/// clipped label, an overlapping control or a squeezed row fails here. Real
/// Roboto metrics are loaded: the default test font draws every glyph one em
/// wide, which would fail layouts that are fine with a real face — and hide
/// nothing that a real face would expose.
void main() {
  setUpAll(() async {
    await initializeTestLocalization();
    await loadGoldenFonts();
  });

  const scales = <double>[1.0, 1.25, 1.5, 2.0];
  const sizes = <String, Size>{
    'compact': Size(400, 900),
    'expanded': Size(1280, 900),
  };

  Future<void> render(
    WidgetTester tester,
    CatalogScreen screen, {
    required double scale,
    required Size size,
    required Locale locale,
  }) async {
    AppClock.pin(now: ScreenCatalog.now, toLocal: (utc) => utc);
    addTearDown(AppClock.reset);
    tester.platformDispatcher.textScaleFactorTestValue = scale;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

    await registerTestServices();
    registerCatalog(screen);
    await pumpCatalogScreen(tester, screen, size: size, locale: locale);
  }

  for (final screen in CatalogScreen.values) {
    for (final scale in scales) {
      for (final locale in const [Locale('en'), Locale('de')]) {
        for (final size in sizes.entries) {
          testWidgets(
            '${screen.name} at ${scale}x, ${locale.languageCode}, ${size.key}',
            (tester) async {
              await render(
                tester,
                screen,
                scale: scale,
                size: size.value,
                locale: locale,
              );
              // An overflow is reported through FlutterError; the test
              // binding fails the test with the full diagnostic (widget,
              // creation site, constraints), so nothing needs taking here.
              expectNoUntranslatedKeys(tester);
            },
          );
        }
      }
    }
  }

  group('the approval path stays usable at 200 %', () {
    for (final locale in const [Locale('en'), Locale('de')]) {
      for (final size in sizes.entries) {
        testWidgets('${locale.languageCode}, ${size.key}', (tester) async {
          await render(
            tester,
            CatalogScreen.humanReview,
            scale: 2,
            size: size.value,
            locale: locale,
          );

          final approve = find.text('humanReview.approveButton'.tr());
          await revealInPage(tester, approve);
          final window = Offset.zero & size.value;
          expect(window.contains(tester.getCenter(approve)), isTrue);

          await tester.tap(approve);
          await tester.pumpAndSettle();

          // Both dialog actions are on screen and reachable, not clipped
          // below the fold of a dialog that grew with its text.
          for (final key in const [
            'humanReview.confirm.cancel',
            'humanReview.confirm.approve',
          ]) {
            final button = find.text(key.tr());
            expect(button, findsOneWidget, reason: key);
            final rect = tester.getRect(button);
            expect(window.contains(rect.topLeft), isTrue, reason: key);
            expect(window.contains(rect.bottomRight), isTrue, reason: key);
          }
        });
      }
    }
  });
}
