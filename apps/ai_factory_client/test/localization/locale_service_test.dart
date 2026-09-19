import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../support/test_app.dart';

/// The locale *preference* rules: resolution order, persistence across a
/// restart, and the reset back to the system default.
void main() {
  setUpAll(initializeTestLocalization);

  group('resolve', () {
    test('explicit English wins over a German device', () {
      expect(
        LocaleService.resolve(LocalePreference.english, const Locale('de')),
        equals(const Locale('en')),
      );
    });

    test('explicit German wins over an English device', () {
      expect(
        LocaleService.resolve(LocalePreference.german, const Locale('en')),
        equals(const Locale('de')),
      );
    });

    test('system follows a supported device locale', () {
      expect(
        LocaleService.resolve(LocalePreference.system, const Locale('de')),
        equals(const Locale('de')),
      );
      expect(
        LocaleService.resolve(LocalePreference.system, const Locale('en')),
        equals(const Locale('en')),
      );
    });

    test('system ignores the country and matches on language only', () {
      expect(
        LocaleService.resolve(
          LocalePreference.system,
          const Locale('de', 'AT'),
        ),
        equals(const Locale('de')),
      );
    });

    test('system falls back to English for an unsupported device locale', () {
      for (final unsupported in const [
        Locale('fa'),
        Locale('fr'),
        Locale('ja'),
      ]) {
        expect(
          LocaleService.resolve(LocalePreference.system, unsupported),
          equals(const Locale('en')),
          reason: 'unsupported locale $unsupported must fall back to English',
        );
      }
    });
  });

  group('persistence', () {
    test('defaults to system when nothing was ever saved', () async {
      SharedPreferences.setMockInitialValues(<String, Object>{});
      final service = await LocaleService().load();

      expect(service.preference, LocalePreference.system);
    });

    test('an explicit choice survives a restart', () async {
      SharedPreferences.setMockInitialValues(<String, Object>{});
      final preferences = await SharedPreferences.getInstance();
      await preferences.setString(
        LocaleService.storageKey,
        LocalePreference.german.storageValue,
      );

      // A fresh service instance stands in for the next application start.
      final service = await LocaleService(preferences).load();

      expect(service.preference, LocalePreference.german);
      expect(
        service.resolveCurrent(const Locale('en')),
        equals(const Locale('de')),
      );
    });

    test('an unrecognised stored value degrades to system', () async {
      SharedPreferences.setMockInitialValues(<String, Object>{
        'flutter.${LocaleService.storageKey}': 'klingon',
      });
      final service = await LocaleService().load();

      expect(service.preference, LocalePreference.system);
    });
  });

  group('apply and reset', () {
    testWidgets('switching to German and back to English is immediate', (
      tester,
    ) async {
      SharedPreferences.setMockInitialValues(<String, Object>{});
      final service = await LocaleService().load();
      late BuildContext capturedContext;

      await tester.pumpWidget(
        _TestLocalizationHost(
          onContext: (context) => capturedContext = context,
        ),
      );
      await tester.pumpAndSettle();

      await service.apply(capturedContext, LocalePreference.german);
      await tester.pumpAndSettle();
      expect(service.preference, LocalePreference.german);

      await service.apply(capturedContext, LocalePreference.english);
      await tester.pumpAndSettle();
      expect(service.preference, LocalePreference.english);
    });

    testWidgets('reset returns the preference to system', (tester) async {
      SharedPreferences.setMockInitialValues(<String, Object>{});
      final service = await LocaleService().load();
      late BuildContext capturedContext;

      await tester.pumpWidget(
        _TestLocalizationHost(
          onContext: (context) => capturedContext = context,
        ),
      );
      await tester.pumpAndSettle();

      await service.apply(capturedContext, LocalePreference.german);
      await tester.pumpAndSettle();

      await service.resetToSystem(capturedContext);
      await tester.pumpAndSettle();

      expect(service.preference, LocalePreference.system);

      // The persisted value is what the next start reads.
      final reloaded = await LocaleService().load();
      expect(reloaded.preference, LocalePreference.system);
    });
  });
}

/// Minimal host that exposes a BuildContext below a localization scope,
/// without pulling in the whole application shell.
class _TestLocalizationHost extends StatelessWidget {
  const _TestLocalizationHost({required this.onContext});

  final void Function(BuildContext context) onContext;

  @override
  Widget build(BuildContext context) {
    return TestLocalizationScope(
      child: Builder(
        builder: (innerContext) {
          onContext(innerContext);
          return const SizedBox.shrink();
        },
      ),
    );
  }
}
