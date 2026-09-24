import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/audit.dart';
import 'package:ai_factory_client/presentation/controllers/audit_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

void main() {
  late FakeAuditRepository audit;

  AuditController build() => AuditController(audit: audit, autoLoad: false);

  setUp(() {
    Get.testMode = true;
    audit = FakeAuditRepository();
  });

  tearDown(() async => Get.deleteAll(force: true));

  test('loads a page of entries', () async {
    final controller = build();

    await controller.load();

    expect(controller.state.value, isA<Success<PagedResult<AuditLogEntry>>>());
    expect(controller.visibleEntries, hasLength(1));
  });

  test('an empty log is Empty, not a failure', () async {
    audit.entries = <AuditLogEntry>[];
    final controller = build();

    await controller.load();

    expect(controller.state.value, isA<Empty<PagedResult<AuditLogEntry>>>());
    expect(controller.visibleEntries, isEmpty);
  });

  test('401 is reported as an operator-access state', () async {
    audit.listError = const AuthenticationError();
    final controller = build();

    await controller.load();

    expect(controller.needsOperatorAccess, isTrue);
  });

  test('a server error is a failure, not an access problem', () async {
    audit.listError = const ServerError();
    final controller = build();

    await controller.load();

    expect(controller.state.value, isA<Failure<PagedResult<AuditLogEntry>>>());
    expect(controller.needsOperatorAccess, isFalse);
  });

  group('severity filter', () {
    test('narrows the loaded page without issuing a request', () async {
      audit.entries = <AuditLogEntry>[
        testAuditLogEntry(id: 'a1', severity: AuditSeverity.info),
        testAuditLogEntry(id: 'a2', severity: AuditSeverity.error),
        testAuditLogEntry(id: 'a3', severity: AuditSeverity.error),
      ];
      final controller = build();
      await controller.load();
      final requestsBefore = audit.listCount;

      controller.filterBySeverity(AuditSeverity.error);

      expect(controller.visibleEntries, hasLength(2));
      expect(
        audit.listCount,
        requestsBefore,
        reason: 'the endpoint ignores severity, so no request may be made',
      );
    });

    test('never sends a severity parameter to the server', () async {
      final controller = build();
      controller.filterBySeverity(AuditSeverity.critical);

      await controller.load();

      // AuditLogQuery has no severity field at all — this asserts the query
      // the repository actually received carries only the real filters.
      final query = audit.queries.single;
      expect(query.projectId, isNull);
      expect(query.page, 1);
      expect(query.pageSize, AuditController.pageSize);
      expect(
        query.toQueryParameters().containsKey('severity'),
        isFalse,
        reason: 'G-AUDIT-SEVERITY: the backend validates then ignores it',
      );
    });

    test('clearing the filter restores the whole page', () async {
      audit.entries = <AuditLogEntry>[
        testAuditLogEntry(id: 'a1', severity: AuditSeverity.info),
        testAuditLogEntry(id: 'a2', severity: AuditSeverity.error),
      ];
      final controller = build();
      await controller.load();

      controller.filterBySeverity(AuditSeverity.error);
      expect(controller.visibleEntries, hasLength(1));

      controller.filterBySeverity(null);
      expect(controller.visibleEntries, hasLength(2));
    });
  });

  group('project filter', () {
    test('is a real server-side query parameter', () async {
      audit.entries = <AuditLogEntry>[
        testAuditLogEntry(id: 'a1', projectId: 'p1'),
        testAuditLogEntry(id: 'a2', projectId: 'p2'),
      ];
      final controller = build();
      await controller.load();

      await controller.filterByProject('p2');

      expect(audit.queries.last.projectId, 'p2');
      expect(controller.visibleEntries, hasLength(1));
      expect(controller.visibleEntries.single.projectId, 'p2');
    });

    test('resets to the first page', () async {
      final controller = build();
      await controller.load();
      controller.page.value = 3;

      await controller.filterByProject('p2');

      expect(controller.page.value, 1);
      expect(audit.queries.last.page, 1);
    });
  });

  group('paging', () {
    test('nextPage does nothing when there is no further page', () async {
      final controller = build();
      await controller.load();
      final requestsBefore = audit.listCount;

      await controller.nextPage();

      expect(controller.page.value, 1);
      expect(audit.listCount, requestsBefore);
    });

    test('previousPage does nothing on the first page', () async {
      final controller = build();
      await controller.load();
      final requestsBefore = audit.listCount;

      await controller.previousPage();

      expect(controller.page.value, 1);
      expect(audit.listCount, requestsBefore);
    });
  });

  test('severity and outcome stay separate fields', () async {
    audit.entries = <AuditLogEntry>[
      testAuditLogEntry(
        severity: AuditSeverity.warning,
        outcome: 'Denied',
        actorType: 'Anonymous',
      ),
    ];
    final controller = build();

    await controller.load();

    final entry = controller.visibleEntries.single;
    expect(entry.severity, AuditSeverity.warning);
    expect(
      entry.outcome,
      'Denied',
      reason: 'a log level is not a business result',
    );
    expect(entry.actorType, 'Anonymous');
  });
}
