/// Regenerates, or checks, the API contract fixtures against a **throwaway**
/// AI Factory API.
///
///     dart run tool/api_fixtures.dart --base-url http://127.0.0.1:5299 --check
///     dart run tool/api_fixtures.dart --base-url http://127.0.0.1:5299 --write
///
/// real API response -> normalized fixture -> committed fixture
///
/// The API must run on a fresh, empty database in a non-production
/// environment (see `docs/flutter-testing.md` for the exact start command).
/// The tool refuses anything else:
///
/// * a base URL that is not loopback, unless `--allow-remote` is given for a
///   dedicated test environment;
/// * an API reporting the `Production` environment;
/// * a database that already holds projects — so no real project data can
///   ever end up in a committed fixture.
///
/// It stores no credential. When the test API requires an operational key,
/// the key is read from `AI_FACTORY_FIXTURE_OPERATIONS_KEY`, sent as a
/// header, and never written anywhere; every capture is also passed through
/// the secret scanner before it is compared or written.
///
/// `--check` exits 1 when the contract of a fresh capture differs from the
/// committed fixtures (see `fixture_normalizer.dart` for what counts).
library;

import 'dart:convert';
import 'dart:io';

import 'fixture_normalizer.dart';
import 'secret_scanner.dart';

const String fixtureDirectory = 'test/fixtures/api';

Future<void> main(List<String> arguments) async {
  final args = _Args.parse(arguments);
  if (args == null) {
    stderr.writeln(
      'usage: dart run tool/api_fixtures.dart --base-url <url> '
      '(--check | --write) [--allow-remote]',
    );
    exitCode = 64;
    return;
  }

  final api = _Api(args.baseUrl, Platform.environment[_keyVariable]);
  try {
    await _guard(api, allowRemote: args.allowRemote);
    final raw = await _capture(api);
    final captured = normalizeForCommit(raw);

    final leaks = [
      for (final e in captured.entries)
        ...scanText(jsonEncode(e.value), path: '${e.key}.json'),
    ];
    if (leaks.isNotEmpty) {
      stderr.writeln('Refusing: the capture contains secret-like values:');
      leaks.forEach(stderr.writeln);
      exitCode = 1;
      return;
    }

    if (args.write) {
      _write(captured);
    } else {
      exitCode = _check(captured) ? 0 : 1;
    }
  } on _Refused catch (e) {
    stderr.writeln('Refusing: ${e.reason}');
    exitCode = 2;
  } finally {
    api.close();
  }
}

const String _keyVariable = 'AI_FACTORY_FIXTURE_OPERATIONS_KEY';

/// The capture scenario. Deterministic: the same requests, in the same
/// order, with the same inputs, on an empty database.
Future<Map<String, Object?>> _capture(_Api api) async {
  final out = <String, Object?>{};
  const criteria = <String, Object?>{
    'version': 1,
    'criteria': [
      {'type': 'BuildPasses'},
    ],
  };

  // A project that stops in Planning, with a plan and a queued agent run.
  final project = await api.expect('POST', '/api/projects', 201, {
    'name': 'Contract Probe',
    'description': 'Phase 19.2 contract capture',
  }) as Map<String, Object?>;
  final id = project['id']! as String;
  await api.expect('POST', '/api/projects/$id/source', 200, {
    'sourceType': 'Empty',
  });
  for (final status in ['Specification', 'Architecture', 'Planning']) {
    await api.expect('PATCH', '/api/projects/$id/status', 200, {
      'status': status,
    });
  }
  final definitions = <String>[];
  for (final type in ['Analysis', 'Implementation']) {
    final definition = await api.expect(
      'POST',
      '/api/agents/definitions',
      201,
      {
        'name': 'probe-$type',
        'description': 'probe',
        'agentType': type,
        'systemPrompt': 'probe',
        'enabled': true,
      },
    ) as Map<String, Object?>;
    definitions.add(definition['id']! as String);
  }
  await api.expect('POST', '/api/projects/$id/plan', 200, {
    'summary': 'Contract probe plan',
    'tasks': [
      {
        'planKey': 'T01',
        'title': 'Analyse',
        'type': 'Analysis',
        'priority': 'High',
        'dependsOn': <String>[],
        'acceptanceCriteria': criteria,
      },
      {
        'planKey': 'T02',
        'title': 'Implement',
        'type': 'Implementation',
        'priority': 'Normal',
        'dependsOn': ['T01'],
        'acceptanceCriteria': criteria,
      },
    ],
  });
  await api.expect('POST', '/api/agents/runs', 201, {
    'agentDefinitionId': definitions.first,
    'projectId': id,
  });

  out['project'] = await api.expect('GET', '/api/projects/$id', 200);
  out['projects_list'] = await api.expect('GET', '/api/projects', 200);
  // Both tasks are created in one transaction with the same timestamp, so
  // the API's order between them is arbitrary; sort for a stable capture.
  out['work_items'] =
      ((await api.expect('GET', '/api/projects/$id/tasks', 200)) as List)
          .cast<Map<String, Object?>>()
          .toList()
        ..sort((a, b) => '${a['title']}'.compareTo('${b['title']}'));
  out['project_plan'] = await api.expect('GET', '/api/projects/$id/plan', 200);
  out['agent_runs_page'] = await api.expect(
    'GET',
    '/api/agents/runs?projectId=$id&page=1&pageSize=5',
    200,
  );
  out['agent_definitions'] = await api.expect(
    'GET',
    '/api/agents/definitions',
    200,
  );
  out['agent_metrics'] = await api.expect('GET', '/api/agents/metrics', 200);
  out['project_diagnostics'] = await api.expect(
    'GET',
    '/api/operations/projects/$id/diagnostics',
    200,
  );
  out['scheduler_status'] = await api.expect(
    'GET',
    '/api/scheduler/status',
    200,
  );
  out['problem_conflict'] = await api.expect(
    'POST',
    '/api/projects/$id/approve',
    409,
    const <String, Object?>{},
  );
  out['problem_validation'] = await api.expect(
    'GET',
    '/api/projects/not-a-guid',
    400,
  );

  // A second project taken through the lifecycle and approved: the
  // approval response is the only way to observe `Completed`.
  final approval = await api.expect('POST', '/api/projects', 201, {
    'name': 'Approval Probe',
    'description': 'Phase 19.10 approval capture',
  }) as Map<String, Object?>;
  final approvalId = approval['id']! as String;
  await api.expect('POST', '/api/projects/$approvalId/source', 200, {
    'sourceType': 'Empty',
  });
  for (final status in [
    'Specification',
    'Architecture',
    'Planning',
    'Implementation',
    'Testing',
    'Reviewing',
    'HumanReview',
  ]) {
    await api.expect('PATCH', '/api/projects/$approvalId/status', 200, {
      'status': status,
    });
  }
  out['approved_project'] = await api.expect(
    'POST',
    '/api/projects/$approvalId/approve',
    200,
    const {'actor': 'contract-probe'},
  );

  out['audit_log_page'] = await api.expect(
    'GET',
    '/api/audit-log?page=1&pageSize=5',
    200,
  );
  out['deployment'] = await api.expect(
    'GET',
    '/api/operations/deployment',
    200,
  );
  out['database'] = await api.expect('GET', '/api/operations/database', 200);
  out['configuration'] = await api.expect(
    'GET',
    '/api/operations/configuration',
    200,
  );
  return out;
}

Future<void> _guard(_Api api, {required bool allowRemote}) async {
  final host = api.baseUri.host;
  const loopback = {'localhost', '127.0.0.1', '::1', '[::1]'};
  if (!loopback.contains(host) && !allowRemote) {
    throw _Refused(
      '$host is not a loopback address. Use a local throwaway API, or pass '
      '--allow-remote for a dedicated test environment.',
    );
  }
  await api.expect('GET', '/health/ready', 200);
  final deployment = await api.expect(
    'GET',
    '/api/operations/deployment',
    200,
  ) as Map<String, Object?>;
  if ('${deployment['environment']}'.toLowerCase() == 'production') {
    throw const _Refused('the API reports the Production environment.');
  }
  final projects = await api.expect('GET', '/api/projects', 200) as List;
  if (projects.isNotEmpty) {
    throw _Refused(
      'the database already holds ${projects.length} project(s). Fixtures are '
      'captured only from an empty, throwaway database.',
    );
  }
}

void _write(Map<String, Object?> captured) {
  for (final e in captured.entries) {
    File('$fixtureDirectory/${e.key}.json')
        .writeAsStringSync('${jsonEncode(e.value)}\n');
  }
  final synthetic = fixtureSpecs.where((s) => s.synthetic).map((s) => s.name);
  stdout.writeln(
    'Wrote ${captured.length} fixtures to $fixtureDirectory. Hand-authored, '
    'left untouched: ${synthetic.join(', ')}.',
  );
}

bool _check(Map<String, Object?> captured) {
  var clean = true;
  for (final spec in fixtureSpecs.where((s) => !s.synthetic)) {
    final file = File('$fixtureDirectory/${spec.name}.json');
    final drift = diffContracts(
      contractShape(spec, jsonDecode(file.readAsStringSync())),
      contractShape(spec, captured[spec.name]),
    );
    if (drift.isEmpty) {
      stdout.writeln('  ok     ${spec.name}');
      continue;
    }
    clean = false;
    stdout.writeln('  DRIFT  ${spec.name}');
    for (final line in drift) {
      stdout.writeln('           $line');
    }
  }

  // The hand-authored structured-result run must still have exactly the
  // fields a captured run has.
  final synthetic = jsonDecode(
    File('$fixtureDirectory/agent_run_structured.json').readAsStringSync(),
  ) as Map<String, Object?>;
  final page = captured['agent_runs_page']! as Map<String, Object?>;
  final live = (page['items']! as List).first as Map<String, Object?>;
  final missing = live.keys.toSet().difference(synthetic.keys.toSet());
  final extra = synthetic.keys.toSet().difference(live.keys.toSet());
  if (missing.isEmpty && extra.isEmpty) {
    stdout.writeln('  ok     agent_run_structured (fields match a live run)');
  } else {
    clean = false;
    stdout.writeln(
      '  DRIFT  agent_run_structured: missing $missing, unknown $extra',
    );
  }
  stdout.writeln(clean ? 'No fixture drift.' : 'Fixture drift detected.');
  return clean;
}

class _Args {
  const _Args(this.baseUrl, {required this.write, required this.allowRemote});

  final String baseUrl;
  final bool write;
  final bool allowRemote;

  static _Args? parse(List<String> args) {
    final i = args.indexOf('--base-url');
    if (i < 0 || i + 1 >= args.length) return null;
    final write = args.contains('--write');
    final check = args.contains('--check');
    if (write == check) return null;
    return _Args(
      args[i + 1],
      write: write,
      allowRemote: args.contains('--allow-remote'),
    );
  }
}

class _Refused implements Exception {
  const _Refused(this.reason);

  final String reason;
}

class _Api {
  _Api(String baseUrl, this._operationsKey) : baseUri = Uri.parse(baseUrl);

  final Uri baseUri;
  final String? _operationsKey;
  final HttpClient _http = HttpClient()
    ..connectionTimeout = const Duration(seconds: 10);

  /// Sends one request and returns its JSON body, failing unless the status
  /// is [status].
  Future<Object?> expect(
    String method,
    String path,
    int status, [
    Object? body,
  ]) async {
    final request = await _http.openUrl(method, baseUri.resolve(path));
    request.headers.set(HttpHeaders.acceptHeader, 'application/json');
    final key = _operationsKey;
    if (key != null && key.isNotEmpty) {
      request.headers.set('X-Operations-Key', key);
    }
    if (body != null) {
      request.headers.contentType = ContentType.json;
      request.write(jsonEncode(body));
    }
    final response = await request.close().timeout(const Duration(seconds: 30));
    final text = await response.transform(utf8.decoder).join();
    if (response.statusCode != status) {
      throw StateError(
        '$method $path answered ${response.statusCode}, expected $status',
      );
    }
    if (text.isEmpty) return null;
    final json = response.headers.contentType?.subType.contains('json');
    return json ?? false ? jsonDecode(text) : text;
  }

  void close() => _http.close(force: true);
}
