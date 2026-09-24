import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../core/localization/locale_service.dart';
import '../core/theme/app_motion.dart';
import '../core/theme/app_theme.dart';
import '../core/theme/theme_service.dart';
import '../presentation/widgets/live_updates.dart';
import 'app_bindings.dart';
import 'app_routes.dart';

/// The root widget.
///
/// Localization delegates come from the enclosing [EasyLocalization] widget
/// (see `main.dart`); `GetMaterialApp` only forwards them. Language is never
/// changed through GetX.
class AiFactoryApp extends StatelessWidget {
  const AiFactoryApp({super.key});

  @override
  Widget build(BuildContext context) {
    return GetMaterialApp(
      title: 'AI Factory',
      debugShowCheckedModeBanner: false,
      localizationsDelegates: context.localizationDelegates,
      supportedLocales: context.supportedLocales,
      locale: context.locale,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: Get.find<ThemeService>().mode,
      initialBinding: AppBindings(),
      initialRoute: AppRoutes.initial,
      getPages: AppRoutes.pages,
      // Lets a live-updating page pause its polling while another page
      // covers it, and refresh the moment it is uncovered.
      navigatorObservers: [liveRouteObserver],
      // Platform transitions, dropped when the OS asks for reduced motion.
      customTransition: AppMotion.pageTransition,
    );
  }
}

/// Wraps [AiFactoryApp] in the localization scope.
///
/// `saveLocale` is left on so the package persists the active locale itself;
/// [LocaleService] persists only the tri-state preference on top of it
/// (System / English / Deutsch), which the package has no concept of.
class AiFactoryAppRoot extends StatelessWidget {
  const AiFactoryAppRoot({this.assetLoader, super.key});

  /// Overridden only by widget tests, which load the same JSON files through
  /// a loader that resolves without real file I/O. Production always uses the
  /// package default (the root asset bundle).
  final AssetLoader? assetLoader;

  @override
  Widget build(BuildContext context) {
    final localeService = Get.find<LocaleService>();

    return EasyLocalization(
      assetLoader: assetLoader ?? const RootBundleAssetLoader(),
      supportedLocales: LocaleService.supportedLocales,
      fallbackLocale: LocaleService.fallbackLocale,
      path: 'assets/translations',
      useFallbackTranslations: true,
      // An explicit choice is applied as the start locale; "system" leaves the
      // decision to the package, which selects from the device locale and
      // falls back to English when the device language is unsupported.
      startLocale: switch (localeService.preference) {
        LocalePreference.english => LocaleService.englishLocale,
        LocalePreference.german => LocaleService.germanLocale,
        LocalePreference.system => null,
      },
      child: const AiFactoryApp(),
    );
  }
}
