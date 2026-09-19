import 'dart:convert';
import 'dart:io';

import 'package:ai_factory_client/core/localization/enum_labels.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

/// Guards the translation assets themselves: both files exist, neither has a
/// key the other lacks, and no value is left empty. A missing German key would
/// otherwise silently fall back to English at runtime.
void main() {
  late Map<String, Object?> en;
  late Map<String, Object?> de;

  setUpAll(() {
    en = _load('assets/translations/en.json');
    de = _load('assets/translations/de.json');
  });

  test('en.json exists and parses', () {
    expect(File('assets/translations/en.json').existsSync(), isTrue);
    expect(en, isNotEmpty);
  });

  test('de.json exists and parses', () {
    expect(File('assets/translations/de.json').existsSync(), isTrue);
    expect(de, isNotEmpty);
  });

  test('both files contain exactly the same keys', () {
    final enKeys = _flatten(en).keys.toSet();
    final deKeys = _flatten(de).keys.toSet();

    expect(
      deKeys.difference(enKeys),
      isEmpty,
      reason: 'de.json has keys that en.json does not define',
    );
    expect(
      enKeys.difference(deKeys),
      isEmpty,
      reason: 'de.json is missing keys that en.json defines',
    );
  });

  test('no translation value is empty', () {
    for (final entry in _flatten(en).entries) {
      expect(entry.value.trim(), isNotEmpty, reason: 'en.json: ${entry.key}');
    }
    for (final entry in _flatten(de).entries) {
      expect(entry.value.trim(), isNotEmpty, reason: 'de.json: ${entry.key}');
    }
  });

  test('German is a real translation, not a copy of English', () {
    // A handful of terms must actually differ; if they do not, the German file
    // was copied rather than translated.
    const mustDiffer = <String>[
      'nav.projects',
      'nav.settings',
      'enums.projectStatus.humanReview',
      'enums.projectStatus.completed',
      'error.network',
    ];

    final flatEn = _flatten(en);
    final flatDe = _flatten(de);

    for (final key in mustDiffer) {
      expect(flatEn[key], isNotNull, reason: 'missing in en.json: $key');
      expect(flatDe[key], isNotNull, reason: 'missing in de.json: $key');
      expect(
        flatDe[key],
        isNot(equals(flatEn[key])),
        reason: '$key is identical in both languages',
      );
    }
  });

  test('every backend enum label key is translated in both languages', () {
    final flatEn = _flatten(en);
    final flatDe = _flatten(de);

    for (final key in EnumLabels.allKeys()) {
      expect(flatEn.containsKey(key), isTrue, reason: 'en.json missing $key');
      expect(flatDe.containsKey(key), isTrue, reason: 'de.json missing $key');
    }
  });

  test(
    'supported locales are exactly English and German, fallback English',
    () {
      expect(
        LocaleService.supportedLocales,
        equals(const <Locale>[Locale('en'), Locale('de')]),
      );
      expect(LocaleService.fallbackLocale, equals(const Locale('en')));
    },
  );
}

Map<String, Object?> _load(String path) =>
    jsonDecode(File(path).readAsStringSync()) as Map<String, Object?>;

/// Flattens the nested JSON into the dotted keys the UI actually calls.
Map<String, String> _flatten(
  Map<String, Object?> source, [
  String prefix = '',
]) {
  final result = <String, String>{};
  source.forEach((key, value) {
    final path = prefix.isEmpty ? key : '$prefix.$key';
    if (value is Map<String, Object?>) {
      result.addAll(_flatten(value, path));
    } else {
      result[path] = value.toString();
    }
  });
  return result;
}
