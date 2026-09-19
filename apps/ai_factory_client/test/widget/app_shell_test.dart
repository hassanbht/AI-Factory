import 'package:ai_factory_client/app/app.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/test_app.dart';

/// Proves the foundation end to end: the shell renders, navigation changes the
/// visible destination, and the whole frame switches language at runtime.
void main() {
  setUpAll(initializeTestLocalization);

  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
  });

  Future<void> pumpApp(WidgetTester tester, {Size? size}) async {
    if (size != null) await setSurfaceSize(tester, size);
    await tester.pumpWidget(
      const AiFactoryAppRoot(assetLoader: SyncFileAssetLoader()),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('shell renders the dashboard destination in English', (
    tester,
  ) async {
    await pumpApp(tester, size: TestSizes.expanded);

    expect(find.text('Dashboard'), findsWidgets);
    expect(find.text('Projects'), findsWidgets);
    expect(find.text('Operations'), findsWidgets);
    expect(find.text('Settings'), findsWidgets);
  });

  testWidgets('navigating to another destination changes the screen', (
    tester,
  ) async {
    await pumpApp(tester, size: TestSizes.expanded);

    // The app bar shows the active destination, so it is the visible proof
    // that the route actually changed.
    expect(
      find.descendant(
        of: find.byType(AppBar),
        matching: find.text('Dashboard'),
      ),
      findsOneWidget,
    );

    await tester.tap(find.text('Projects').first);
    await tester.pumpAndSettle();

    expect(
      find.descendant(of: find.byType(AppBar), matching: find.text('Projects')),
      findsOneWidget,
    );
  });

  testWidgets('switching to German translates the whole shell', (tester) async {
    await pumpApp(tester, size: TestSizes.expanded);

    expect(find.text('Projects'), findsWidgets);
    expect(find.text('Projekte'), findsNothing);

    final context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Projekte'), findsWidgets);
    expect(find.text('Einstellungen'), findsWidgets);
    expect(find.text('Betrieb'), findsWidgets);
    expect(find.text('Projects'), findsNothing);
  });

  testWidgets('switching back from German to English needs no restart', (
    tester,
  ) async {
    await pumpApp(tester, size: TestSizes.expanded);

    var context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();
    expect(find.text('Übersicht'), findsWidgets);

    context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.english);
    await tester.pumpAndSettle();

    expect(find.text('Dashboard'), findsWidgets);
    expect(find.text('Übersicht'), findsNothing);
  });

  testWidgets('settings screen renders its sections in both languages', (
    tester,
  ) async {
    await pumpApp(tester, size: TestSizes.expanded);

    await tester.tap(find.text('Settings').first);
    await tester.pumpAndSettle();

    expect(find.text('Appearance and language'), findsOneWidget);
    expect(find.text('Connection'), findsOneWidget);

    final context = tester.element(find.byType(Scaffold).first);
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Darstellung und Sprache'), findsOneWidget);
    expect(find.text('Verbindung'), findsOneWidget);
  });

  testWidgets('no translation key leaks into the rendered UI', (tester) async {
    await pumpApp(tester, size: TestSizes.expanded);

    // A missing key renders as the key itself; catching that here is cheaper
    // than noticing it in a screenshot later.
    final texts = tester.widgetList<Text>(find.byType(Text));
    for (final text in texts) {
      final value = text.data;
      if (value == null) continue;
      expect(
        RegExp(r'^[a-z][a-zA-Z]*(\.[a-zA-Z]+)+$').hasMatch(value),
        isFalse,
        reason: 'untranslated key rendered: $value',
      );
    }
  });

  // One pumped application per test: re-pumping inside a single test reuses
  // the navigator, so the second iteration would start on whatever screen the
  // first one navigated to.
  for (final entry in <String, Size>{
    'compact': TestSizes.compact,
    'tablet': TestSizes.medium,
    'desktop': TestSizes.expanded,
  }.entries) {
    testWidgets('German labels do not overflow at ${entry.key} width', (
      tester,
    ) async {
      await pumpApp(tester, size: entry.value);

      final context = tester.element(find.byType(Scaffold).first);
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      expect(find.text('Einstellungen'), findsWidgets);
      await tester.tap(find.text('Einstellungen').first);
      await tester.pumpAndSettle();

      // A RenderFlex overflow surfaces as a thrown exception in tests.
      expect(
        tester.takeException(),
        isNull,
        reason: 'German layout overflowed at ${entry.value}',
      );
    });
  }
}
