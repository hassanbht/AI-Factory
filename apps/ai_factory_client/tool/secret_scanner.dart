/// A secret scanner for the Flutter client's sources, tests, fixtures,
/// assets and build output.
///
/// It looks for **secret values**, never for secret **names**: a reference
/// such as `OPENROUTER_API_KEY`, `X-Operations-Key` or
/// `ApiHeaders.operationsKey` is how the code talks about a credential and is
/// always allowed; a value that looks like the credential itself is not.
///
/// Pure Dart (no Flutter), so CI runs it with `dart run` and the test suite
/// imports the same rules.
library;

import 'dart:io';
import 'dart:math' as math;

/// One detection rule.
class SecretRule {
  const SecretRule(this.id, this.description, this.pattern, {this.check});

  final String id;
  final String description;
  final RegExp pattern;

  /// An extra judgement on the match, for rules whose pattern alone cannot
  /// tell a value from a reference (see [looksLikeSecretValue]).
  final bool Function(RegExpMatch match)? check;
}

class SecretFinding {
  const SecretFinding(this.path, this.line, this.rule, this.excerpt);

  final String path;
  final int line;
  final SecretRule rule;

  /// The line with the matched value masked — a finding report must not
  /// itself leak the secret.
  final String excerpt;

  @override
  String toString() => '$path:$line [${rule.id}] ${rule.description}: $excerpt';
}

final List<SecretRule> secretRules = <SecretRule>[
  SecretRule(
    'private-key',
    'PEM private key',
    RegExp(
      r'-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY',
    ),
  ),
  SecretRule(
    'openrouter-key',
    'OpenRouter API key',
    RegExp(r'\bsk-or-v1-[0-9a-fA-F]{32,}'),
  ),
  SecretRule(
    'anthropic-key',
    'Anthropic API key',
    RegExp(r'\bsk-ant-[A-Za-z0-9_-]{20,}'),
  ),
  SecretRule(
    'openai-style-key',
    'sk- prefixed provider key',
    RegExp(r'\bsk-(?:proj-)?[A-Za-z0-9]{20,}'),
  ),
  SecretRule(
    'github-token',
    'GitHub token',
    RegExp(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})'),
  ),
  SecretRule(
    'aws-access-key',
    'AWS access key',
    RegExp(r'\bAKIA[0-9A-Z]{16}\b'),
  ),
  SecretRule(
    'google-api-key',
    'Google API key',
    RegExp(r'\bAIza[0-9A-Za-z_-]{35}\b'),
  ),
  SecretRule(
    'slack-token',
    'Slack token',
    RegExp(r'\bxox[abprs]-[A-Za-z0-9-]{10,}'),
  ),
  SecretRule(
    'jwt',
    'JSON Web Token',
    RegExp(
      r'\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}',
    ),
  ),
  SecretRule(
    'bearer-token',
    'Bearer token value',
    RegExp(r'\bBearer\s+([A-Za-z0-9._~+/=-]{20,})'),
    check: (m) => looksLikeSecretValue(m.group(1)!),
  ),
  SecretRule(
    'env-assignment',
    '.env-style secret assignment',
    RegExp(
      r'^\s*(?:export\s+)?[A-Z][A-Z0-9_]*(?:KEY|SECRET|TOKEN|PASSWORD|PASSWD)'
      r'\s*=\s*"?([^\s"#]{6,})',
      multiLine: true,
    ),
    check: (m) => looksLikeSecretValue(m.group(1)!),
  ),
  SecretRule(
    'credential-literal',
    'credential-named field holding a literal',
    RegExp(
      r'''(?:api[_-]?key|apikey|secret|password|passwd|token|access[_-]?key|operations[_-]?key)["']?\s*[:=]\s*["']([^"'\s]{8,})["']''',
      caseSensitive: false,
    ),
    check: (m) => looksLikeSecretValue(m.group(1)!),
  ),
];

/// A value, not a reference: long enough, varied enough, and not one of the
/// shapes the code uses to *name* a secret.
bool looksLikeSecretValue(String value) {
  // Masks and placeholders.
  if (RegExp(r'^[*•x.<>\[\]{}-]+$', caseSensitive: false).hasMatch(value)) {
    return false;
  }
  if (RegExp(
    r'redacted|placeholder|example|changeme|your[_-]',
    caseSensitive: false,
  ).hasMatch(value)) {
    return false;
  }
  // SCREAMING_CASE environment-variable names and dotted translation keys.
  if (RegExp(r'^[A-Z][A-Z0-9_]*$').hasMatch(value)) return false;
  if (RegExp(r'^[a-z][a-zA-Z0-9]*(\.[a-zA-Z0-9]+)+$').hasMatch(value)) {
    return false;
  }
  // Interpolation and code (`$key`, `_field.text`, `ApiHeaders.x`).
  if (value.contains(r'$') || value.contains('(')) return false;
  // Names — header names (`X-Operations-Key`), identifiers, words — have no
  // digits; a generated credential of this length practically always does.
  // Provider keys with a known prefix are caught by their own rules above.
  if (!RegExp(r'\d').hasMatch(value)) return false;
  return value.length >= 12 && _entropy(value) >= 3.0;
}

/// Shannon entropy in bits per character.
double _entropy(String value) {
  final counts = <int, int>{};
  for (final unit in value.codeUnits) {
    counts[unit] = (counts[unit] ?? 0) + 1;
  }
  var entropy = 0.0;
  for (final count in counts.values) {
    final p = count / value.length;
    entropy -= p * math.log(p) / math.ln2;
  }
  return entropy;
}

/// Scans one text for secret values. [path] is only used in the report.
List<SecretFinding> scanText(String text, {String path = '<text>'}) {
  final findings = <SecretFinding>[];
  for (final rule in secretRules) {
    for (final match in rule.pattern.allMatches(text)) {
      final check = rule.check;
      if (check != null && !check(match)) continue;
      final line = '\n'.allMatches(text.substring(0, match.start)).length + 1;
      final lineStart = text.lastIndexOf('\n', match.start) + 1;
      var lineEnd = text.indexOf('\n', match.end);
      if (lineEnd < 0) lineEnd = text.length;
      final masked =
          '${text.substring(lineStart, match.start)}[REDACTED]'
          '${text.substring(match.end, lineEnd)}';
      findings.add(SecretFinding(path, line, rule, masked.trim()));
    }
  }
  return findings;
}

/// Extensions read as text. Images and fonts are skipped: they cannot hold a
/// typed credential, and goldens are rendered from key-free catalog data.
const Set<String> scannedExtensions = <String>{
  '.dart',
  '.json',
  '.yaml',
  '.yml',
  '.md',
  '.txt',
  '.env',
  '.html',
  '.js',
  '.mjs',
  '.css',
  '.xml',
  '.plist',
  '.properties',
  '.gradle',
  '.kts',
  '.ps1',
  '.sh',
  '.cmd',
  '.bat',
  '.arb',
  '.toml',
  '.ini',
  '.cfg',
  '.map',
};

bool isScannedPath(String path) {
  final name = path.split(RegExp(r'[\\/]')).last.toLowerCase();
  if (name.startsWith('.env')) return true;
  final dot = name.lastIndexOf('.');
  return dot >= 0 && scannedExtensions.contains(name.substring(dot));
}

/// Scans every text file under [roots]. Missing roots are skipped.
List<SecretFinding> scanPaths(Iterable<String> roots) {
  final findings = <SecretFinding>[];
  for (final root in roots) {
    final files = switch (FileSystemEntity.typeSync(root)) {
      FileSystemEntityType.file => [File(root)],
      FileSystemEntityType.directory => Directory(
        root,
      ).listSync(recursive: true).whereType<File>().toList(),
      _ => const <File>[],
    };
    for (final file in files) {
      if (!isScannedPath(file.path)) continue;
      final String text;
      try {
        text = file.readAsStringSync();
      } on FileSystemException {
        continue; // not UTF-8 text
      }
      findings.addAll(scanText(text, path: file.path));
    }
  }
  return findings;
}
