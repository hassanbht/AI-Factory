import 'dart:convert';
import 'dart:io';

import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/core/theme/app_theme.dart';
import 'package:ai_factory_client/core/theme/theme_service.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:shared_preferences/shared_preferences.dart';

/// Shared test scaffolding: a real localization scope backed by the real
/// translation assets, so tests fail when a key is missing rather than
/// silently rendering the key name.
Future<void> initializeTestLocalization() async {
  TestWidgetsFlutterBinding.ensureInitialized();
  SharedPreferences.setMockInitialValues(<String, Object>{});
  await EasyLocalization.ensureInitialized();
}

/// Registers the services the application expects before the first frame and
/// returns the locale service so a test can switch language at runtime.
Future<LocaleService> registerTestServices() async {
  await Get.deleteAll(force: true);

  final localeService = await LocaleService().load();
  Get.put<LocaleService>(localeService, permanent: true);

  final themeService = await ThemeService().load();
  Get.put<ThemeService>(themeService, permanent: true);

  Get.put<AppConfig>(
    const AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: 'http://localhost:5223',
      connectTimeout: Duration(seconds: 10),
      sendTimeout: Duration(seconds: 30),
      receiveTimeout: Duration(seconds: 30),
    ),
    permanent: true,
  );

  return localeService;
}

/// Reads the real translation JSON from disk without going through the root
/// asset bundle.
///
/// `rootBundle` resolves through real I/O, which the fake-async zone a widget
/// test runs in cannot advance — the localization delegate would stay pending
/// and `MaterialApp` would render an empty frame forever. Reading the same
/// files synchronously keeps the tests deterministic while still exercising
/// the actual translations.
class SyncFileAssetLoader extends AssetLoader {
  const SyncFileAssetLoader();

  @override
  Future<Map<String, dynamic>?> load(String path, Locale locale) async {
    final file = File('$path/${locale.languageCode}.json');
    if (!file.existsSync()) return <String, dynamic>{};
    return jsonDecode(file.readAsStringSync()) as Map<String, dynamic>;
  }
}

/// Localization scope without the application shell, for unit-style widget
/// tests that only need a context below `EasyLocalization`.
class TestLocalizationScope extends StatelessWidget {
  const TestLocalizationScope({
    required this.child,
    this.startLocale,
    super.key,
  });

  final Widget child;
  final Locale? startLocale;

  @override
  Widget build(BuildContext context) {
    return EasyLocalization(
      assetLoader: const SyncFileAssetLoader(),
      supportedLocales: LocaleService.supportedLocales,
      fallbackLocale: LocaleService.fallbackLocale,
      path: 'assets/translations',
      startLocale: startLocale,
      useFallbackTranslations: true,
      child: Builder(
        builder: (innerContext) => MaterialApp(
          localizationsDelegates: innerContext.localizationDelegates,
          supportedLocales: innerContext.supportedLocales,
          locale: innerContext.locale,
          home: child,
        ),
      ),
    );
  }
}

/// Sets the logical window size for a single test and restores it afterwards.
Future<void> setSurfaceSize(WidgetTester tester, Size size) async {
  await tester.binding.setSurfaceSize(size);
  tester.view.physicalSize = size * tester.view.devicePixelRatio;
  addTearDown(() async {
    await tester.binding.setSurfaceSize(null);
    tester.view.resetPhysicalSize();
  });
}

/// Common window sizes matching the breakpoint model.
class TestSizes {
  const TestSizes._();

  static const Size compact = Size(420, 900);
  static const Size medium = Size(800, 900);
  static const Size expanded = Size(1440, 900);
}

/// Localization scope built on `GetMaterialApp`, for screens that use GetX
/// navigation, `Obx` and `Get.find`.
///
/// It is the production widget tree minus the routing table: the page under
/// test is the home route, so a test drives the real screen with real
/// translations and a controller it staged itself.
class TestAppScope extends StatelessWidget {
  const TestAppScope({
    this.home,
    this.startLocale,
    this.initialRoute,
    this.getPages,
    super.key,
  });

  final Widget? home;
  final Locale? startLocale;

  /// Supplied instead of [home] when a test drives the real routing table,
  /// which is the only way to exercise navigation between screens.
  final String? initialRoute;
  final List<GetPage<dynamic>>? getPages;

  @override
  Widget build(BuildContext context) {
    return EasyLocalization(
      assetLoader: const SyncFileAssetLoader(),
      supportedLocales: LocaleService.supportedLocales,
      fallbackLocale: LocaleService.fallbackLocale,
      path: 'assets/translations',
      startLocale: startLocale,
      useFallbackTranslations: true,
      child: Builder(
        builder: (innerContext) => GetMaterialApp(
          localizationsDelegates: innerContext.localizationDelegates,
          supportedLocales: innerContext.supportedLocales,
          locale: innerContext.locale,
          theme: AppTheme.light(),
          home: home,
          initialRoute: initialRoute,
          getPages: getPages,
        ),
      ),
    );
  }
}

/// Pumps [home] inside [TestAppScope] at the given window size and settles.
Future<void> pumpScreen(
  WidgetTester tester, {
  Widget? home,
  String? initialRoute,
  List<GetPage<dynamic>>? getPages,
  Size size = TestSizes.expanded,
  Locale? startLocale,
}) async {
  Get.testMode = true;
  await setSurfaceSize(tester, size);
  await tester.pumpWidget(
    TestAppScope(
      home: home,
      startLocale: startLocale,
      initialRoute: initialRoute,
      getPages: getPages,
    ),
  );
  await tester.pumpAndSettle();
}

/// Fails the test if any visible `Text` still holds a raw translation key.
void expectNoUntranslatedKeys(WidgetTester tester) {
  final pattern = RegExp(r'^[a-z][a-zA-Z]*(\.[a-zA-Z]+)+$');
  for (final text in tester.widgetList<Text>(find.byType(Text))) {
    final value = text.data;
    if (value == null) continue;
    expect(
      pattern.hasMatch(value),
      isFalse,
      reason: 'untranslated key rendered: $value',
    );
  }
}
