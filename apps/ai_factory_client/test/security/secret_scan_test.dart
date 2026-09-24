import 'package:flutter_test/flutter_test.dart';

import '../../tool/secret_scanner.dart';

/// The secret scan that CI runs (`dart run tool/secret_scan.dart`), and
/// proof that it can tell a secret **value** from a secret **reference**.
///
/// Every synthetic secret below is assembled at runtime from fragments, so
/// this file itself never contains a match — the repository scan at the end
/// covers `test/` too.
void main() {
  String join(List<String> parts) => parts.join();

  group('detects secret values', () {
    final samples = <String, String>{
      'openrouter-key': join(['sk-or-v1-', 'a3f9' * 16]),
      'anthropic-key': join(['sk-ant-', 'api03-', 'Zx8q' * 8]),
      'openai-style-key': join(['sk-', 'Q7wR' * 6]),
      'github-token': join(['gh', 'p_', 'A1b2C3d4' * 5]),
      'aws-access-key': join(['AK', 'IA', 'QX7RT2Z9LM4K8P3N']),
      'private-key': join(['-----BEGIN ', 'RSA PRIVATE', ' KEY-----']),
      'jwt': join([
        'ey',
        'JhbGciOiJIUzI1NiJ9.',
        'ey',
        'JzdWIiOiIxMjM0NTY3ODkwIn0.',
        'dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      ]),
    };

    for (final sample in samples.entries) {
      test(sample.key, () {
        final findings = scanText('const value = "${sample.value}";');
        expect(findings.map((f) => f.rule.id), contains(sample.key));
      });
    }

    test('a bearer token value', () {
      final token = join(['9fQ2', 'x7Lk', 'P3mZ', 'v8Rt', 'W1cN', '5bHa']);
      expect(scanText('Authorization: Bearer $token'), isNotEmpty);
    });

    test('a .env assignment', () {
      final value = join(['r8Tq', '2mZx', '9LpW', '4vKc']);
      expect(scanText('OPENROUTER_API_KEY=$value\n'), isNotEmpty);
      expect(scanText('export HERMES_TOKEN="$value"\n'), isNotEmpty);
    });

    test('an operational key hard-coded into a header map', () {
      final value = join(['c2F1', 'cmVk', 'LWtl', 'eS0x', 'MjM0']);
      final findings = scanText("headers: {'operationsKey': '$value'}");
      expect(findings, isNotEmpty);
    });

    test('a finding never repeats the secret it found', () {
      final key = join(['sk-or-v1-', 'b4e1' * 16]);
      final finding = scanText('key = "$key"').first;
      expect(finding.toString(), isNot(contains(key)));
      expect(finding.excerpt, contains('[REDACTED]'));
    });
  });

  group('allows secret references', () {
    const references = <String>[
      "static const String operationsKey = 'X-Operations-Key';",
      'OPENROUTER_API_KEY',
      r'final key = Platform.environment["OPENROUTER_API_KEY"];',
      "'x-api-key': ApiHeaders.operationsKey,",
      "label: 'operatorAccess.field'.tr(),",
      'OPENROUTER_API_KEY=',
      'OPENROUTER_API_KEY=<your-key>',
      r'password: $password',
      "token: '***',",
      'Authorization: Bearer <token>',
      'The key is sent in the X-Operations-Key header.',
      '"OpenRouter:ApiKey"',
    ];

    for (final reference in references) {
      test(reference, () => expect(scanText(reference), isEmpty));
    }
  });

  test('the Flutter client contains no secret value', () {
    // The same roots CI scans. Goldens and fonts are binary and are not
    // read; goldens are rendered from key-free catalog data.
    final findings = scanPaths(const [
      'lib',
      'test',
      'integration_test',
      'assets',
      'tool',
      'pubspec.yaml',
    ]);
    expect(findings, isEmpty, reason: findings.join('\n'));
  });
}
