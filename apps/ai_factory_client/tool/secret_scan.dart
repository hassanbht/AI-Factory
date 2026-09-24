/// Command-line secret scan.
///
///     dart run tool/secret_scan.dart            # lib, test, integration_test, assets
///     dart run tool/secret_scan.dart build/web  # any other paths, e.g. a build
///
/// Exits 1 and prints each finding (value masked) when anything is found.
library;

import 'dart:io';

import 'secret_scanner.dart';

const List<String> defaultRoots = <String>[
  'lib',
  'test',
  'integration_test',
  'assets',
  'tool',
  'pubspec.yaml',
];

void main(List<String> arguments) {
  final roots = arguments.isEmpty ? defaultRoots : arguments;
  final findings = scanPaths(roots);

  if (findings.isEmpty) {
    stdout.writeln('Secret scan: clean (${roots.join(', ')}).');
    return;
  }
  stderr.writeln('Secret scan: ${findings.length} finding(s):');
  for (final finding in findings) {
    stderr.writeln('  $finding');
  }
  exitCode = 1;
}
