/// Normalization and drift comparison for the API contract fixtures.
///
/// Two transformations, both pure:
///
/// * [normalizeForCommit] turns raw captures into what is committed under
///   `test/fixtures/api/`: every GUID becomes a stable sequence GUID, every
///   timestamp a fixed instant **in the same format** (a trailing `Z` stays,
///   a missing `Z` stays missing — the backend's documented quirk), hashes a
///   stable hex string, and machine-specific text (OS, runtime, uptime) a
///   placeholder. Order and equality between values survive: the second
///   timestamp is still later than the first, and the project id in a
///   problem detail is still the project's id.
/// * [contractShape] turns a fixture into its **contract**: structure, field
///   names, JSON types, enum spellings, nullability and the timestamp format,
///   with identifiers, instants and per-environment values reduced to typed
///   tokens. Drift is a difference between the contract of a fresh capture
///   and the contract of the committed file ([diffContracts]).
///
/// Pure Dart; used by `tool/api_fixtures.dart` and by the test suite.
library;

import 'dart:convert';

/// How one fixture is captured and compared.
class FixtureSpec {
  const FixtureSpec(
    this.name, {
    this.volatile = const <String>{},
    this.anyValue = const <String>{},
    this.shapeOnly = const <String>{},
    this.synthetic = false,
  });

  /// File name without `.json`.
  final String name;

  /// Paths whose value legitimately differs between captures (counters,
  /// runtime descriptions): compared by JSON type only.
  final Set<String> volatile;

  /// Paths that may be null in one environment and set in another (build
  /// metadata): not compared at all.
  final Set<String> anyValue;

  /// Arrays whose *content* depends on the deployment's configuration:
  /// compared by the field names their elements carry (or, for scalar
  /// arrays, by element type), not element by element.
  final Set<String> shapeOnly;

  /// Hand-authored because no endpoint can produce it without a real agent
  /// execution. Its shape is still checked against a captured sibling.
  final bool synthetic;
}

/// Every committed fixture. Paths: `a.b` for a field, `a[]` for each element.
const List<FixtureSpec> fixtureSpecs = <FixtureSpec>[
  FixtureSpec('project'),
  FixtureSpec('projects_list'),
  FixtureSpec('work_items'),
  FixtureSpec('project_plan'),
  FixtureSpec('agent_runs_page'),
  FixtureSpec('agent_run_structured', synthetic: true),
  FixtureSpec('agent_definitions'),
  FixtureSpec(
    'agent_metrics',
    volatile: {
      'queued',
      'running',
      'created',
      'started',
      'succeeded',
      'failed',
      'timedOut',
      'cancelled',
      'workerClaimSuccess',
    },
    anyValue: {'averageDurationMs', 'totalDurationMs'},
  ),
  FixtureSpec('project_diagnostics'),
  FixtureSpec(
    'deployment',
    volatile: {
      'informationalVersion',
      'frameworkDescription',
      'osDescription',
      'processArchitecture',
      'uptime',
    },
    anyValue: {'gitCommit', 'gitBranch', 'buildTimestamp', 'buildNumber'},
  ),
  FixtureSpec(
    'database',
    volatile: {
      'operationsCount',
      'failuresCount',
      'retriesCount',
      'transientFailuresCount',
      'concurrencyConflictsCount',
      'busyFailuresCount',
      'lockedFailuresCount',
      'transactionFailuresCount',
      'averageDurationMs',
    },
  ),
  FixtureSpec(
    'configuration',
    shapeOnly: {
      'externalProviders',
      'configurationSources',
      'resilience.profileNames',
    },
    volatile: {'resilience.configuredProfilesCount'},
  ),
  FixtureSpec(
    'scheduler_status',
    volatile: {
      'totalDispatched',
      'totalFailures',
      'lastCycleDispatchedCount',
      'lastCycleFailureCount',
      'lastCycleSkippedDueToConcurrency',
    },
    anyValue: {'lastCycleAtUtc', 'lastCycleProjectId'},
  ),
  FixtureSpec('audit_log_page'),
  FixtureSpec('approved_project'),
  FixtureSpec('problem_validation'),
  FixtureSpec('problem_conflict'),
];

FixtureSpec specFor(String name) =>
    fixtureSpecs.firstWhere((s) => s.name == name);

final RegExp _guid = RegExp(
  r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}',
);
final RegExp _timestamp = RegExp(
  r'^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.(\d+))?(Z|[+-]\d\d:\d\d)?$',
);
final RegExp _hash = RegExp(r'^[0-9A-Fa-f]{64}$');
final RegExp _timeSpan = RegExp(r'^\d{2}:\d{2}:\d{2}(\.\d+)?$');

/// Fixed placeholders for machine-specific text in committed fixtures.
const Map<String, String> _machinePlaceholders = <String, String>{
  'deployment.informationalVersion': '1.0.0+normalized',
  'deployment.frameworkDescription': '.NET (normalized)',
  'deployment.osDescription': 'OS (normalized)',
  'deployment.uptime': '00:00:00',
};

/// Normalizes a whole capture — every fixture in one pass, so a GUID or an
/// instant maps to the same replacement in every file.
Map<String, Object?> normalizeForCommit(Map<String, Object?> fixtures) {
  final guids = <String, String>{};
  final instants = <DateTime>{};

  void collect(Object? node) {
    switch (node) {
      case final Map<String, Object?> map:
        map.values.forEach(collect);
      case final List<Object?> list:
        list.forEach(collect);
      case final String text:
        for (final m in _guid.allMatches(text)) {
          guids.putIfAbsent(
            m.group(0)!.toLowerCase(),
            () =>
                '00000000-0000-4000-8000-'
                '${(guids.length + 1).toString().padLeft(12, '0')}',
          );
        }
        final instant = _parseInstant(text);
        if (instant != null) instants.add(instant);
    }
  }

  fixtures.values.forEach(collect);

  // Instants keep their order: the n-th distinct instant becomes
  // 2026-01-01T00:00:00 + n seconds.
  final base = DateTime.utc(2026);
  final ordered = instants.toList()..sort();
  final replacement = <DateTime, DateTime>{
    for (var i = 0; i < ordered.length; i++)
      ordered[i]: base.add(Duration(seconds: i)),
  };

  var hashes = 0;
  final hashMap = <String, String>{};

  Object? rewrite(Object? node, String path) {
    switch (node) {
      case final Map<String, Object?> map:
        return <String, Object?>{
          for (final e in map.entries)
            e.key: rewrite(e.value, path.isEmpty ? e.key : '$path.${e.key}'),
        };
      case final List<Object?> list:
        return [for (final v in list) rewrite(v, '$path[]')];
      case final String text:
        final placeholder = _machinePlaceholders[path];
        if (placeholder != null) return placeholder;
        final match = _timestamp.firstMatch(text);
        final instant = _parseInstant(text);
        if (match != null && instant != null) {
          return _formatLike(replacement[instant]!, match);
        }
        if (_hash.hasMatch(text)) {
          return hashMap.putIfAbsent(
            text,
            () => (++hashes).toRadixString(16).toUpperCase().padLeft(64, '0'),
          );
        }
        return text.replaceAllMapped(
          _guid,
          (m) => guids[m.group(0)!.toLowerCase()]!,
        );
      default:
        return node;
    }
  }

  return <String, Object?>{
    for (final e in fixtures.entries) e.key: rewrite(e.value, e.key),
  };
}

DateTime? _parseInstant(String text) {
  final match = _timestamp.firstMatch(text);
  if (match == null) return null;
  // The backend's no-`Z` timestamps are UTC too (see UtcDateTimeConverter).
  final withZone = match.group(4) == null ? '${text}Z' : text;
  return DateTime.tryParse(withZone)?.toUtc();
}

/// Renders [instant] with the same fraction length and zone suffix as the
/// original.
String _formatLike(DateTime instant, RegExpMatch original) {
  final seconds = instant.toIso8601String().substring(0, 19);
  final digits = original.group(3)?.length ?? 0;
  final fraction = digits == 0 ? '' : '.${'0' * digits}';
  final hasZone = original.group(4) != null;
  return '$seconds$fraction${hasZone ? 'Z' : ''}';
}

/// The contract of one fixture: what must not change between backend
/// releases without the client knowing.
Object? contractShape(FixtureSpec spec, Object? json) {
  Object? walk(Object? node, String path) {
    if (spec.anyValue.contains(path)) return '<any>';
    if (spec.shapeOnly.contains(path)) {
      // Which entries exist depends on configuration, and so does which
      // optional fields are null; the field names do not.
      final list = node is List<Object?> ? node : const <Object?>[];
      final fields = <String>{
        for (final v in list)
          if (v is Map<String, Object?>) ...v.keys,
      };
      final scalars = <String>{
        for (final v in list)
          if (v is! Map<String, Object?>) jsonEncode(_shapeOf(v)),
      };
      return <String, Object?>{
        '<fields>': fields.toList()..sort(),
        '<scalars>': scalars.toList()..sort(),
      };
    }
    final isVolatile = spec.volatile.contains(path);
    switch (node) {
      case final Map<String, Object?> map:
        final keys = map.keys.toList()..sort();
        return <String, Object?>{
          for (final k in keys) k: walk(map[k], path.isEmpty ? k : '$path.$k'),
        };
      case final List<Object?> list:
        return [for (final v in list) walk(v, '$path[]')];
      case final String text:
        if (isVolatile) {
          return _timeSpan.hasMatch(text) ? '<timespan>' : '<string>';
        }
        return _tokenizeString(text);
      case final num value:
        return isVolatile ? '<number>' : value;
      case final bool value:
        return isVolatile ? '<bool>' : value;
      default:
        return node;
    }
  }

  return walk(json, '');
}

String _tokenizeString(String text) {
  final match = _timestamp.firstMatch(text);
  if (match != null) {
    return match.group(4) == null ? '<timestamp>' : '<timestamp-utc>';
  }
  if (_hash.hasMatch(text)) return '<hash>';
  return text.replaceAll(_guid, '<guid>');
}

Object? _shapeOf(Object? node) => switch (node) {
  final Map<String, Object?> map => <String, Object?>{
    for (final k in map.keys.toList()..sort()) k: _shapeOf(map[k]),
  },
  final List<Object?> list => [
    ...{for (final v in list) jsonEncode(_shapeOf(v))},
  ],
  String() => '<string>',
  num() => '<number>',
  bool() => '<bool>',
  null => null,
  _ => '<unknown>',
};

/// Human-readable differences between two contracts; empty means no drift.
List<String> diffContracts(
  Object? expected,
  Object? actual, [
  String at = r'$',
]) {
  if (expected is Map<String, Object?> && actual is Map<String, Object?>) {
    return [
      for (final key in {...expected.keys, ...actual.keys})
        if (!expected.containsKey(key))
          '$at.$key: new field (${jsonEncode(actual[key])})'
        else if (!actual.containsKey(key))
          '$at.$key: field removed (was ${jsonEncode(expected[key])})'
        else
          ...diffContracts(expected[key], actual[key], '$at.$key'),
    ];
  }
  if (expected is List<Object?> && actual is List<Object?>) {
    if (expected.length != actual.length) {
      return [
        '$at: ${expected.length} elements expected, ${actual.length} found',
      ];
    }
    return [
      for (var i = 0; i < expected.length; i++)
        ...diffContracts(expected[i], actual[i], '$at[$i]'),
    ];
  }
  if (expected == actual) return const [];
  return ['$at: expected ${jsonEncode(expected)}, found ${jsonEncode(actual)}'];
}
