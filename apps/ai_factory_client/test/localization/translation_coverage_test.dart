import 'dart:convert';
import 'dart:io';

import 'package:ai_factory_client/core/localization/enum_labels.dart';
import 'package:ai_factory_client/presentation/models/project_list_filter.dart';
import 'package:flutter_test/flutter_test.dart';

/// Every translation key the source calls must exist in both language files,
/// every key in the files must be used, and every value must carry the
/// placeholders its call site fills.
///
/// The widget tests catch a missing key only on the screens they render; this
/// scans the whole of `lib/` and therefore covers the branches a test happens
/// not to exercise — an error state, a rarely-hit enum, a state a later phase
/// added. A missing key renders as the key itself, which is the kind of defect
/// that reaches a screenshot rather than a test run.
void main() {
  late Map<String, String> en;
  late Map<String, String> de;
  late String source;
  late List<_KeyUse> uses;

  setUpAll(() {
    en = _flatten(_load('assets/translations/en.json'));
    de = _flatten(_load('assets/translations/de.json'));
    source = _readDart(Directory('lib'));
    uses = _collectUses(source);
  });

  Set<String> usedKeys() => {for (final use in uses) use.key};

  /// A plural key is stored as `key.one` / `key.other`; a call names `key`.
  bool defines(Map<String, String> file, String key) =>
      file.containsKey(key) || file.containsKey('$key.other');

  test('the scan finds the keys it is supposed to check', () {
    // A guard on the guard: if the pattern stopped matching, every other
    // assertion here would pass vacuously.
    expect(usedKeys().length, greaterThan(300));
    expect(usedKeys(), contains('common.refresh'));
    expect(usedKeys(), contains('projects.action.start'));
    expect(uses.where((u) => u.plural), isNotEmpty);
  });

  test('every literal key used in lib/ exists in English', () {
    final missing = usedKeys().where((k) => !defines(en, k)).toList()..sort();
    expect(missing, isEmpty, reason: 'en.json is missing: $missing');
  });

  test('every literal key used in lib/ exists in German', () {
    final missing = usedKeys().where((k) => !defines(de, k)).toList()..sort();
    expect(missing, isEmpty, reason: 'de.json is missing: $missing');
  });

  test('no orphan key: every key in the files is used by lib/', () {
    // Keys built at runtime, each from a closed Dart enum.
    final dynamicKeys = <String>{
      ...EnumLabels.allKeys(),
      for (final filter in ProjectStatusFilter.values) filter.labelKey,
    };
    final orphans = <String>[
      for (final key in en.keys)
        if (!_isUsed(_pluralBase(key), source) &&
            !dynamicKeys.contains(_pluralBase(key)))
          key,
    ]..sort();
    expect(orphans, isEmpty, reason: 'unused translations: $orphans');
  });

  test('plural keys are only called with .plural(), and vice versa', () {
    for (final use in uses) {
      final isPluralKey = en.containsKey('${use.key}.other');
      expect(
        use.plural,
        isPluralKey,
        reason:
            '${use.key} is ${isPluralKey ? '' : 'not '}a plural key but is '
            'called with .${use.plural ? 'plural' : 'tr'}()',
      );
    }
  });

  test('every plural key has "one" and "other" in both languages', () {
    final pluralKeys = {
      for (final key in en.keys)
        if (key.endsWith('.other')) _pluralBase(key),
    };
    expect(pluralKeys, isNotEmpty);
    for (final key in pluralKeys) {
      for (final file in [en, de]) {
        expect(file.containsKey('$key.one'), isTrue, reason: '$key.one');
        expect(file.containsKey('$key.other'), isTrue, reason: '$key.other');
      }
    }
  });

  test('placeholders match between the two languages', () {
    // `{}` is positional: a German string with fewer placeholders than the
    // English one silently drops a value the operator needed. Named
    // placeholders (`{name}`) must be the same set.
    for (final entry in en.entries) {
      final german = de[entry.key];
      if (german == null) continue;
      expect(
        _positional(german),
        _positional(entry.value),
        reason: 'positional placeholder count differs for ${entry.key}',
      );
      expect(
        _named(german),
        _named(entry.value),
        reason: 'named placeholders differ for ${entry.key}',
      );
    }
  });

  test('no translation is malformed: braces balance, no stray key names', () {
    for (final file in {'en': en, 'de': de}.entries) {
      for (final entry in file.value.entries) {
        final value = entry.value;
        expect(
          '{'.allMatches(value).length,
          '}'.allMatches(value).length,
          reason: '${file.key}: unbalanced braces in ${entry.key}',
        );
        expect(
          RegExp(r'^[a-z]+(\.[a-zA-Z]+)+$').hasMatch(value),
          isFalse,
          reason: '${file.key}: ${entry.key} holds a key, not a text',
        );
      }
    }
  });

  test('each call site passes as many args as its text has placeholders', () {
    final mismatches = <String>[];
    for (final use in uses) {
      final text = use.plural ? en['${use.key}.other'] : en[use.key];
      if (text == null) continue; // reported by the "exists" tests
      final placeholders = _positional(text);
      final args = use.argCount;
      if (args == null) {
        // `.plural(n)` fills a single `{}` with the count itself.
        final filled = use.plural ? 1 : 0;
        if (placeholders > filled) {
          mismatches.add('${use.key}: no args, $placeholders placeholders');
        }
      } else if (args != placeholders) {
        mismatches.add('${use.key}: $args args, $placeholders placeholders');
      }
    }
    expect(mismatches, isEmpty);
  });

  test('no hard-coded user-visible string in a Text or a label', () {
    // Literal text in the widgets that render words an operator reads. The
    // only allowed literal is the product name, which is not translated.
    final pattern = RegExp(
      r'''(?:Text|SelectableText)\(\s*'([^'$]*[A-Za-z]{2,}[^'$]*)'(?!\s*\.)|'''
      r'''(?:label|tooltip|hintText|labelText|helperText|semanticsLabel|message|title):\s*'([^'$]*[A-Za-z]{2,}[^'$]*)'(?!\s*\.)''',
    );
    const allowed = {'AI Factory'};
    final found = <String>[];
    for (final file in _dartFiles(Directory('lib'))) {
      for (final match in pattern.allMatches(file.readAsStringSync())) {
        final text = match.group(1) ?? match.group(2)!;
        if (allowed.contains(text)) continue;
        found.add('${file.path}: "$text"');
      }
    }
    expect(found, isEmpty);
  });
}

class _KeyUse {
  const _KeyUse(this.key, {required this.plural, this.argCount});

  final String key;
  final bool plural;

  /// Elements of an `args: [...]` list, or null when none is passed.
  final int? argCount;
}

Map<String, Object?> _load(String path) =>
    jsonDecode(File(path).readAsStringSync()) as Map<String, Object?>;

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

String _pluralBase(String key) =>
    key.replaceFirst(RegExp(r'\.(zero|one|two|few|many|other)$'), '');

bool _isUsed(String key, String source) =>
    source.contains("'$key'") || source.contains('"$key"');

int _positional(String text) => '{}'.allMatches(text).length;

Set<String> _named(String text) => {
  for (final m in RegExp(r'\{([a-zA-Z]\w*)\}').allMatches(text)) m.group(1)!,
};

Iterable<File> _dartFiles(Directory root) => root
    .listSync(recursive: true)
    .whereType<File>()
    .where((f) => f.path.endsWith('.dart'));

String _readDart(Directory root) =>
    _dartFiles(root).map((f) => f.readAsStringSync()).join('\n');

/// `'some.key'.tr(` / `'some.key'.plural(` — the only forms the application
/// uses for a literal key, including when the formatter breaks the line
/// before the dot. Keys built at runtime are enum-derived and checked above.
final RegExp _literalKey = RegExp(
  r"'([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)'\s*\.(tr|plural)\(",
);

List<_KeyUse> _collectUses(String source) => [
  for (final match in _literalKey.allMatches(source))
    _KeyUse(
      match.group(1)!,
      plural: match.group(2) == 'plural',
      argCount: _argsListLength(_callArguments(source, match.end - 1)),
    ),
];

/// The text between the call's parentheses; [open] indexes the `(`.
String _callArguments(String source, int open) {
  var depth = 0;
  for (var i = open; i < source.length; i++) {
    final c = source[i];
    if ('([{'.contains(c)) depth++;
    if (')]}'.contains(c)) depth--;
    if (depth == 0) return source.substring(open + 1, i);
  }
  return '';
}

/// Top-level element count of an `args: [...]` list, or null without one.
int? _argsListLength(String call) {
  final start = call.indexOf('args:');
  if (start < 0) return null;
  final open = call.indexOf('[', start);
  var depth = 0;
  var commas = 0;
  var trailingComma = false;
  var empty = true;
  for (var i = open; i < call.length; i++) {
    final c = call[i];
    if ('([{'.contains(c)) {
      depth++;
      if (depth > 1) empty = false;
      continue;
    }
    if (')]}'.contains(c)) {
      depth--;
      if (depth == 0) {
        if (empty) return 0;
        return trailingComma ? commas : commas + 1;
      }
      continue;
    }
    if (c.trim().isEmpty) continue;
    if (depth == 1 && c == ',') {
      commas++;
      trailingComma = true;
      continue;
    }
    empty = false;
    trailingComma = false;
  }
  return null;
}
