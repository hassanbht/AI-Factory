import 'dart:convert';
import 'dart:io';

import 'package:ai_factory_client/data/dto/agent_dto.dart';
import 'package:ai_factory_client/data/dto/audit_dto.dart';
import 'package:ai_factory_client/data/dto/project_dto.dart';
import 'package:ai_factory_client/data/mappers/mappers.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../tool/fixture_normalizer.dart';
import '../support/api_fixtures.dart';

/// The committed API contract fixtures, and the drift detector that guards
/// them.
///
/// The live half — capture a fresh API, compare — runs in CI's contract job
/// (`tool/api_fixtures.dart --check`). This file is the credential-free half
/// that runs with every `flutter test`: the fixture set is complete, it is
/// normalized (no real ids, instants or machine details), every fixture
/// parses into the domain, the documented serialization quirks are present,
/// and the comparison flags every kind of drift while ignoring what changes
/// naturally.
void main() {
  const directory = 'test/fixtures/api';

  Object? load(String name) =>
      jsonDecode(File('$directory/$name.json').readAsStringSync());

  group('the fixture set', () {
    test('every file has a spec and every spec has a file', () {
      final files = Directory(directory)
          .listSync()
          .whereType<File>()
          .where((f) => f.path.endsWith('.json'))
          .map((f) => f.uri.pathSegments.last.replaceAll('.json', ''))
          .toSet();
      expect(files, {for (final s in fixtureSpecs) s.name});
    });

    test('covers every contract area the client consumes', () {
      expect(
        {for (final s in fixtureSpecs) s.name},
        <String>{
          'project', // Project
          'projects_list',
          'work_items', // WorkItem
          'project_plan', // ProjectPlan
          'agent_runs_page', // AgentRun
          'agent_run_structured', // StructuredAgentResult
          'agent_definitions', // AgentDefinition
          'project_diagnostics', // Diagnostics
          'deployment', // Deployment
          'database', // Database
          'configuration', // Configuration
          'scheduler_status', // Scheduler
          'agent_metrics', // Metrics
          'audit_log_page', // AuditLog
          'approved_project', // Approval
          'problem_validation', // representative errors
          'problem_conflict',
        },
      );
    });

    test('is normalized: no real id, instant, hash or machine detail', () {
      final guid = RegExp(
        r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-'
        r'[0-9a-fA-F]{12}',
      );
      final normalizedGuid = RegExp(r'^00000000-0000-4000-8000-\d{12}$');
      final instant = RegExp(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d');
      for (final spec in fixtureSpecs) {
        final text = File('$directory/${spec.name}.json').readAsStringSync();
        for (final m in guid.allMatches(text)) {
          expect(
            normalizedGuid.hasMatch(m.group(0)!),
            isTrue,
            reason: '${spec.name}: ${m.group(0)}',
          );
        }
        for (final m in instant.allMatches(text)) {
          expect(
            m.group(0)!.startsWith('2026-01-01T00:'),
            isTrue,
            reason: '${spec.name}: ${m.group(0)}',
          );
        }
      }
      final deployment = load('deployment')! as Map<String, Object?>;
      expect(deployment['osDescription'], 'OS (normalized)');
      expect(deployment['frameworkDescription'], '.NET (normalized)');
      expect(deployment['uptime'], '00:00:00');
    });
  });

  group('serialization quirks the API really has', () {
    test('UTC timestamps both without and with a trailing Z', () {
      final runs = load('agent_runs_page')! as Map<String, Object?>;
      final run = (runs['items']! as List).first as Map<String, Object?>;
      expect(run['createdAtUtc'], isNot(endsWith('Z')));
      final database = load('database')! as Map<String, Object?>;
      expect(database['generatedAtUtc'], endsWith('Z'));
      // One object, both formats: the approval response.
      final approved = load('approved_project')! as Map<String, Object?>;
      expect(approved['createdAtUtc'], isNot(endsWith('Z')));
      expect(approved['updatedAtUtc'], endsWith('Z'));
    });

    test('numeric enum ordinals in the audit log', () {
      final page = load('audit_log_page')! as Map<String, Object?>;
      final entry = (page['items']! as List).first as Map<String, Object?>;
      expect(entry['eventType'], isA<int>());
      expect(entry['severity'], isA<int>());
    });

    test('nullable failure categories and optional structured results', () {
      final runs = load('agent_runs_page')! as Map<String, Object?>;
      final run = (runs['items']! as List).first as Map<String, Object?>;
      expect(run.containsKey('failureCategory'), isTrue);
      expect(run['failureCategory'], isNull);
      expect(run['structuredResult'], isNull);
      final structured = load('agent_run_structured')! as Map<String, Object?>;
      final result = structured['structuredResult']! as Map<String, Object?>;
      expect(result['data'], isNull);
    });
  });

  group('the new fixtures map into the domain', () {
    test('approval response: Completed', () {
      final project = mapProject(
        ProjectDto.fromJson(ApiFixtures.object(ApiFixtures.approvedProject)),
      );
      expect(project.status, ProjectStatus.completed);
      expect(project.updatedAtUtc.isUtc, isTrue);
      expect(project.createdAtUtc.isUtc, isTrue);
    });

    test('audit log page: ordinals resolved', () {
      final page = mapAuditLogPage(
        AuditLogPageDto.fromJson(ApiFixtures.object(ApiFixtures.auditLogPage)),
      );
      expect(page.items, isNotEmpty);
      final entry = page.items.first;
      expect(entry.timestampUtc.isUtc, isTrue);
      expect(entry.actorType, 'Human');
    });

    test('structured result run', () {
      final run = mapAgentRun(
        AgentRunDto.fromJson(
          ApiFixtures.object(ApiFixtures.agentRunStructured),
        ),
      );
      expect(run.status, AgentRunStatus.succeeded);
      expect(run.duration, const Duration(seconds: 30));
      final result = run.structuredResult;
      expect(result, isNotNull);
      expect(result?.status, StructuredResultStatus.succeeded);
      expect(result?.warnings, hasLength(1));
      expect(result?.artifacts, ['docs/analysis.md']);
    });
  });

  group('drift detection', () {
    final spec = specFor('project');
    late Map<String, Object?> base;

    setUp(() => base = load('project')! as Map<String, Object?>);

    List<String> drift(
      Map<String, Object?> Function(Map<String, Object?>) change,
    ) => diffContracts(
      contractShape(spec, base),
      contractShape(spec, change(Map.of(base))),
    );

    test('a committed fixture matches itself', () {
      expect(drift((j) => j), isEmpty);
    });

    test('ignores ids and instants that naturally change', () {
      expect(
        drift(
          (j) => j
            ..['id'] = '11111111-2222-4333-8444-555555555555'
            ..['createdAtUtc'] = '2031-05-06T07:08:09.1234567',
        ),
        isEmpty,
      );
    });

    test('fails on a new field', () {
      expect(drift((j) => j..['ownerId'] = null), [contains('new field')]);
    });

    test('fails on a removed field', () {
      expect(drift((j) => j..remove('gitBranch')), [contains('removed')]);
    });

    test('fails on a changed enum representation', () {
      expect(drift((j) => j..['status'] = 3), isNotEmpty);
      expect(drift((j) => j..['status'] = 'planning'), isNotEmpty);
    });

    test('fails when a timestamp gains or loses its Z', () {
      expect(
        drift((j) => j..['createdAtUtc'] = '${j['createdAtUtc']}Z'),
        isNotEmpty,
      );
    });

    test('fails on a type change and on a nullability change', () {
      expect(drift((j) => j..['name'] = 42), isNotEmpty);
      expect(drift((j) => j..['gitRepository'] = 'https://x.test/r.git'), [
        contains('gitRepository'),
      ]);
    });

    test('compares per-environment values by type only', () {
      final metrics = specFor('agent_metrics');
      final committed = load('agent_metrics')! as Map<String, Object?>;
      final busier = Map.of(committed)..['queued'] = 999;
      expect(
        diffContracts(
          contractShape(metrics, committed),
          contractShape(metrics, busier),
        ),
        isEmpty,
      );
      final retyped = Map.of(committed)..['queued'] = 'many';
      expect(
        diffContracts(
          contractShape(metrics, committed),
          contractShape(metrics, retyped),
        ),
        isNotEmpty,
      );
    });

    test('compares configuration-dependent lists by their field names', () {
      final config = specFor('configuration');
      final committed = load('configuration')! as Map<String, Object?>;
      final providers = (committed['externalProviders']! as List)
          .cast<Map<String, Object?>>();
      final fewer = Map.of(committed)
        ..['externalProviders'] = [providers.first];
      expect(
        diffContracts(
          contractShape(config, committed),
          contractShape(config, fewer),
        ),
        isEmpty,
      );
      final reshaped = Map.of(committed)
        ..['externalProviders'] = [
          {...providers.first, 'apiVersion': 2},
        ];
      expect(
        diffContracts(
          contractShape(config, committed),
          contractShape(config, reshaped),
        ),
        isNotEmpty,
      );
    });
  });

  group('normalization for commit', () {
    test('keeps order, equality and the timestamp format', () {
      final normalized = normalizeForCommit({
        'a': {
          'id': 'AAAAAAAA-0000-4000-8000-000000000123',
          'created': '2026-09-19T19:02:11.4453985',
          'updated': '2026-09-19T19:02:39.6215309Z',
        },
        'b': {
          'detail': "Project 'aaaaaaaa-0000-4000-8000-000000000123' failed.",
          'hash': 'B63176D59F622571DF27A8F485237948D2E34BDDA46BEB3F0A0C927EEA71D289',
        },
      });
      final a = normalized['a']! as Map<String, Object?>;
      final b = normalized['b']! as Map<String, Object?>;
      expect(a['id'], '00000000-0000-4000-8000-000000000001');
      // The same GUID inside a message maps to the same replacement.
      expect(b['detail'], contains('00000000-0000-4000-8000-000000000001'));
      expect(a['created'], '2026-01-01T00:00:00.0000000');
      expect(a['updated'], '2026-01-01T00:00:01.0000000Z');
      expect(b['hash'], hasLength(64));
      expect(b['hash'], isNot(startsWith('B6')));
    });
  });
}
