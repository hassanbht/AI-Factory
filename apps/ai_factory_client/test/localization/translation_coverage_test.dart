import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Every translation key the source calls must exist in both language files.
///
/// The widget tests catch a missing key only on the screens they render; this
/// scans the whole of `lib/` and therefore covers the branches a test happens
/// not to exercise — an error state, a rarely-hit enum, a state a later phase
/// added. A missing key renders as the key itself, which is the kind of defect
/// that reaches a screenshot rather than a test run.
void main() {
  late Map<String, String> en;
  late Map<String, String> de;
  late Set<String> usedKeys;

  setUpAll(() {
    en = _flatten(_load('assets/translations/en.json'));
    de = _flatten(_load('assets/translations/de.json'));
    usedKeys = _collectKeys(Directory('lib'));
  });

  test('the scan finds the keys it is supposed to check', () {
    // A guard on the guard: if the pattern stopped matching, every other
    // assertion here would pass vacuously.
    expect(usedKeys.length, greaterThan(50));
    expect(usedKeys, contains('common.refresh'));
    expect(usedKeys, contains('projects.action.start'));
  });

  test('every literal key used in lib/ exists in English', () {
    final missing = usedKeys.where((key) => !en.containsKey(key)).toList()
      ..sort();
    expect(missing, isEmpty, reason: 'en.json is missing: $missing');
  });

  test('every literal key used in lib/ exists in German', () {
    final missing = usedKeys.where((key) => !de.containsKey(key)).toList()
      ..sort();
    expect(missing, isEmpty, reason: 'de.json is missing: $missing');
  });

  test('a placeholder count matches between the two languages', () {
    // `{}` is positional: a German string with fewer placeholders than the
    // English one silently drops a value the operator needed.
    for (final entry in en.entries) {
      final german = de[entry.key];
      if (german == null) continue;
      expect(
        '{}'.allMatches(german).length,
        '{}'.allMatches(entry.value).length,
        reason: 'placeholder count differs for ${entry.key}',
      );
    }
  });
}

Map<String, Object?> _load(String path) =>
    jsonDecode(File(path).readAsStringSync()) as Map<String, Object?>;

Map<String, String> _flatten(Map<String, Object?> source, [String prefix = '']) {
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

/// Matches `'some.key'.tr(` — the only form the application uses for a
/// literal key. Keys built at runtime (`'enums.projectStatus.$name'`) are
/// covered by `EnumLabels.allKeys()` in `translation_files_test.dart`.
final RegExp _literalKey = RegExp(r"'([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)'\s*\.tr\(");

Set<String> _collectKeys(Directory root) {
  final keys = <String>{};
  for (final entity in root.listSync(recursive: true)) {
    if (entity is! File || !entity.path.endsWith('.dart')) continue;
    for (final match in _literalKey.allMatches(entity.readAsStringSync())) {
      keys.add(match.group(1)!);
    }
  }
  return keys;
}
