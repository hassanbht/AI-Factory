import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;
import 'package:shared_preferences/shared_preferences.dart';

/// Persists the operator's theme choice. Deliberately shaped like
/// [LocaleService] so both preferences behave the same way.
class ThemeService extends GetxService {
  ThemeService([this._preferences]);

  static const String storageKey = 'settings.theme_mode';

  SharedPreferences? _preferences;

  final Rx<ThemeMode> _mode = ThemeMode.system.obs;

  ThemeMode get mode => _mode.value;

  Rx<ThemeMode> get modeListenable => _mode;

  Future<ThemeService> load() async {
    _preferences ??= await SharedPreferences.getInstance();
    _mode.value = _parse(_preferences?.getString(storageKey));
    return this;
  }

  Future<void> apply(ThemeMode mode) async {
    _mode.value = mode;
    _preferences ??= await SharedPreferences.getInstance();
    await _preferences?.setString(storageKey, mode.name);
    Get.changeThemeMode(mode);
  }

  static ThemeMode _parse(String? value) {
    return switch (value) {
      'light' => ThemeMode.light,
      'dark' => ThemeMode.dark,
      _ => ThemeMode.system,
    };
  }
}
