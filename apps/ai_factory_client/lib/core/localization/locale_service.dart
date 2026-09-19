import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/widgets.dart';
import 'package:get/get.dart' hide Trans;
import 'package:shared_preferences/shared_preferences.dart';

/// What the operator explicitly chose, which is not the same thing as the
/// locale currently in effect: "system" plus a German device produces German,
/// but it must keep following the device if that changes.
///
/// `easy_localization` persists the *locale*; this service persists the
/// *preference* — the one piece of state the package cannot express — and
/// drives the package through its own runtime APIs.
enum LocalePreference {
  system('system'),
  english('en'),
  german('de');

  const LocalePreference(this.storageValue);

  final String storageValue;

  static LocalePreference fromStorage(String? value) {
    if (value == null) return LocalePreference.system;
    for (final preference in LocalePreference.values) {
      if (preference.storageValue == value) return preference;
    }
    return LocalePreference.system;
  }
}

/// Owns the language preference. `easy_localization` remains the localization
/// engine; GetX is only used to make this service injectable and observable.
class LocaleService extends GetxService {
  LocaleService([this._preferences]);

  static const String storageKey = 'settings.locale_preference';

  static const Locale englishLocale = Locale('en');
  static const Locale germanLocale = Locale('de');

  static const List<Locale> supportedLocales = <Locale>[
    englishLocale,
    germanLocale,
  ];

  static const Locale fallbackLocale = englishLocale;

  SharedPreferences? _preferences;

  final Rx<LocalePreference> _preference = LocalePreference.system.obs;

  LocalePreference get preference => _preference.value;

  Rx<LocalePreference> get preferenceListenable => _preference;

  /// Reads the persisted preference. Safe to call before `runApp`.
  Future<LocaleService> load() async {
    _preferences ??= await SharedPreferences.getInstance();
    _preference.value = LocalePreference.fromStorage(
      _preferences?.getString(storageKey),
    );
    return this;
  }

  /// Resolves a preference against the device locale. Pure and synchronous, so
  /// the rule is testable without a widget tree.
  ///
  /// * [LocalePreference.system] follows the device when its language is
  ///   supported, otherwise falls back to English.
  /// * An explicit choice always wins.
  static Locale resolve(LocalePreference preference, Locale deviceLocale) {
    switch (preference) {
      case LocalePreference.english:
        return englishLocale;
      case LocalePreference.german:
        return germanLocale;
      case LocalePreference.system:
        for (final locale in supportedLocales) {
          if (locale.languageCode == deviceLocale.languageCode) return locale;
        }
        return fallbackLocale;
    }
  }

  /// The locale a given preference produces right now, for display purposes.
  Locale resolveCurrent(Locale deviceLocale) =>
      resolve(_preference.value, deviceLocale);

  /// Applies a preference and switches the UI language immediately.
  ///
  /// `setLocale` / `deleteSaveLocale` / `resetLocale` are the package's own
  /// runtime APIs — this service adds persistence of the tri-state preference
  /// and nothing else.
  Future<void> apply(BuildContext context, LocalePreference preference) async {
    final target = resolve(preference, context.deviceLocale);

    if (preference == LocalePreference.system) {
      // Drop the locale easy_localization saved for an earlier explicit
      // choice, then let it re-select from the device locale.
      await context.deleteSaveLocale();
      if (!context.mounted) return;
      await context.resetLocale();
    } else {
      await context.setLocale(target);
    }

    // GetMaterialApp resolves its locale as `Get.locale ?? locale`, so GetX's
    // copy has to follow easy_localization — otherwise the MaterialApp keeps
    // the language it was first built with and every `.tr()` below it stays
    // stale. This is the only locale state GetX holds: the decision, the
    // translations and the persistence all belong to easy_localization and to
    // this service.
    Get.locale = target;

    // Persisted last: the preference only becomes the remembered one once the
    // switch actually succeeded.
    await _persist(preference);
  }

  /// Returns to following the operating system.
  Future<void> resetToSystem(BuildContext context) =>
      apply(context, LocalePreference.system);

  Future<void> _persist(LocalePreference preference) async {
    _preference.value = preference;
    _preferences ??= await SharedPreferences.getInstance();
    await _preferences?.setString(storageKey, preference.storageValue);
  }
}
