import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// The Operations screen, driven through the real route and its bindings.
void main() {
  setUpAll(initializeTestLocalization);

  late FakeOperationsRepository operations;
  late FakeDiagnosticsRepository diagnostics;
  late FakeAuditRepository audit;
  late InMemoryOperationalCredentialStore credentials;

  setUp(() async {
    await registerTestServices();
    operations = FakeOperationsRepository();
    diagnostics = FakeDiagnosticsRepository();
    audit = FakeAuditRepository();
    credentials = InMemoryOperationalCredentialStore();

    Get.put<ProjectRepository>(FakeProjectRepository());
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    Get.put<ApprovalRepository>(FakeApprovalRepository());
    Get.put<OperationsRepository>(operations);
    Get.put<DiagnosticsRepository>(diagnostics);
    Get.put<AuditRepository>(audit);
    Get.put<OperationalCredentialStore>(credentials);
    Get.put<ApiClient>(ApiClient(config: Get.find()));
  });

  Future<void> open(
    WidgetTester tester, {
    Size size = TestSizes.expanded,
    Locale? locale,
  }) => pumpScreen(
    tester,
    initialRoute: AppRoutes.operations,
    getPages: AppRoutes.pages,
    size: size,
    startLocale: locale,
  );

  /// Drags the Operations list until [finder] is mounted and hit-testable.
  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isEmpty) {
      final list = find.byType(ListView).first;
      for (var i = 0; i < 14; i++) {
        await tester.drag(list, const Offset(0, -500));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) break;
      }
    }
    await tester.ensureVisible(finder);
    await tester.pumpAndSettle();
  }

  group('sections', () {
    testWidgets('renders every section when every endpoint answers', (
      tester,
    ) async {
      await open(tester);

      expect(find.text('API health'), findsOneWidget);
      expect(find.text('Deployment'), findsOneWidget);
      expect(find.text('Database'), findsOneWidget);

      await reveal(tester, find.text('Scheduler activity'));
      expect(find.text('Scheduler activity'), findsOneWidget);
      expect(find.text('Agent metrics'), findsOneWidget);
    });

    testWidgets('health is reported as API health, never as system health', (
      tester,
    ) async {
      await open(tester);

      expect(find.text('API health'), findsOneWidget);
      expect(
        find.textContaining('System healthy'),
        findsNothing,
        reason: 'only the API answered, so only the API may be claimed',
      );
    });

    testWidgets('states worker health is unavailable', (tester) async {
      await open(tester);

      expect(find.text('Worker health'), findsOneWidget);
      expect(find.textContaining('no worker health endpoint'), findsOneWidget);
    });

    testWidgets('labels scheduler as activity, not worker health', (
      tester,
    ) async {
      await open(tester);
      await reveal(tester, find.text('Scheduler activity'));

      expect(find.text('This is not worker health'), findsOneWidget);
      expect(find.textContaining('Worker healthy'), findsNothing);
    });

    testWidgets('states model routing is unavailable', (tester) async {
      await open(tester);
      await reveal(tester, find.text('Model routing'));

      expect(
        find.textContaining('Model routing information is unavailable'),
        findsOneWidget,
      );
    });
  });

  group('partial availability', () {
    testWidgets('an unauthorized section leaves the others rendered', (
      tester,
    ) async {
      diagnostics.metricsError = const AuthenticationError();
      await open(tester);

      // The sections that did load are still on screen.
      expect(find.text('API health'), findsOneWidget);
      expect(find.text('Deployment'), findsOneWidget);

      await reveal(tester, find.text('Agent metrics'));
      expect(
        find.text('Operator access required'),
        findsWidgets,
        reason: '401 must read as an access state, not a server failure',
      );
    });

    testWidgets('a failed section shows a retry that reloads only it', (
      tester,
    ) async {
      operations.deploymentError = const ServerError();
      await open(tester);

      expect(find.text('This section could not be loaded.'), findsOneWidget);

      final databaseCallsBefore = operations.databaseCount;
      operations.deploymentError = null;

      await tester.tap(find.text('Retry').first);
      await tester.pumpAndSettle();

      expect(find.text('This section could not be loaded.'), findsNothing);
      expect(
        operations.databaseCount,
        databaseCallsBefore,
        reason: 'a per-section retry must not refetch the whole screen',
      );
    });

    testWidgets('a whole-API outage still renders the page frame', (
      tester,
    ) async {
      operations
        ..healthError = const NetworkError()
        ..deploymentError = const NetworkError()
        ..databaseError = const NetworkError()
        ..configurationError = const NetworkError()
        ..schedulerError = const NetworkError();
      diagnostics.metricsError = const NetworkError();
      audit.listError = const NetworkError();

      await open(tester);

      expect(find.text('Operations'), findsWidgets);
      expect(find.text('This section could not be loaded.'), findsWidgets);
    });
  });

  group('operator access', () {
    testWidgets('the key field is obscured', (tester) async {
      await open(tester);
      await reveal(tester, find.text('Operator access'));

      final field = tester.widget<TextField>(find.byType(TextField).last);
      expect(
        field.obscureText,
        isTrue,
        reason: 'a credential must not be shown while typing',
      );
    });

    testWidgets('saving a key stores it and reloads the sections', (
      tester,
    ) async {
      diagnostics.metricsError = const AuthenticationError();
      await open(tester);
      await reveal(tester, find.text('Operator access'));

      diagnostics.metricsError = null;
      await tester.enterText(find.byType(TextField).last, 'test-key-0001');
      await tester.tap(find.text('Save key'));
      await tester.pumpAndSettle();

      expect(await credentials.read(), 'test-key-0001');
      expect(find.text('A key is stored for this device.'), findsOneWidget);
    });

    testWidgets('clearing the key forgets it', (tester) async {
      await credentials.write('test-key-0001');
      await open(tester);
      await reveal(tester, find.text('Operator access'));

      await tester.tap(find.text('Clear key'));
      await tester.pumpAndSettle();

      expect(await credentials.read(), isNull);
      expect(find.text('No key is stored for this device.'), findsOneWidget);
    });

    testWidgets('the stored key is never rendered on screen', (tester) async {
      await credentials.write('super-secret-key-value');
      await open(tester);
      await reveal(tester, find.text('Operator access'));

      expect(
        find.textContaining('super-secret-key-value'),
        findsNothing,
        reason: 'not even masked — the panel reports presence only',
      );
    });
  });

  group('audit log', () {
    testWidgets('renders entries with severity and event type', (tester) async {
      audit.entries = [
        testAuditLogEntry(
          eventType: 'HumanApproval',
          severity: AuditSeverity.warning,
          message: 'Approval recorded by an operator.',
        ),
      ];
      await open(tester);
      await reveal(tester, find.text('Audit log'));

      expect(find.text('HumanApproval'), findsOneWidget);
      expect(find.text('Warning'), findsWidgets);
      expect(find.text('Approval recorded by an operator.'), findsOneWidget);
    });

    testWidgets('says the severity filter is client-side', (tester) async {
      await open(tester);
      await reveal(tester, find.text('Audit log'));

      expect(
        find.textContaining('narrows the loaded page only'),
        findsOneWidget,
      );
    });

    testWidgets('never renders the raw metadata blob', (tester) async {
      await open(tester);
      await reveal(tester, find.text('Audit log'));

      expect(find.textContaining('metadataJson'), findsNothing);
    });
  });

  group('responsive', () {
    for (final (name, size) in <(String, Size)>[
      ('compact', TestSizes.compact),
      ('medium', TestSizes.medium),
      ('expanded', TestSizes.expanded),
    ]) {
      testWidgets('renders without overflow at $name width', (tester) async {
        await open(tester, size: size);

        expect(find.text('API health'), findsOneWidget);
        expect(tester.takeException(), isNull);
      });
    }
  });

  group('localization', () {
    testWidgets('renders German', (tester) async {
      await open(tester, locale: const Locale('de'));

      expect(find.text('API-Zustand'), findsOneWidget);
      expect(find.text('Datenbank'), findsOneWidget);
    });

    testWidgets('no untranslated keys in German', (tester) async {
      await open(tester, locale: const Locale('de'));

      final list = find.byType(ListView).first;
      for (var i = 0; i < 14; i++) {
        await tester.drag(list, const Offset(0, -500));
        await tester.pumpAndSettle();
      }

      expectNoUntranslatedKeys(tester);
    });

    testWidgets('no untranslated keys in English', (tester) async {
      await open(tester);

      final list = find.byType(ListView).first;
      for (var i = 0; i < 14; i++) {
        await tester.drag(list, const Offset(0, -500));
        await tester.pumpAndSettle();
      }

      expectNoUntranslatedKeys(tester);
    });
  });
}
