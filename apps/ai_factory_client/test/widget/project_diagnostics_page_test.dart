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

void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

  late FakeDiagnosticsRepository diagnostics;
  late InMemoryOperationalCredentialStore credentials;

  setUp(() async {
    await registerTestServices();
    diagnostics = FakeDiagnosticsRepository();
    credentials = InMemoryOperationalCredentialStore();

    Get.put<ProjectRepository>(
      FakeProjectRepository(projects: [testProject(id: projectId)]),
    );
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    Get.put<ApprovalRepository>(FakeApprovalRepository());
    Get.put<OperationsRepository>(FakeOperationsRepository());
    Get.put<DiagnosticsRepository>(diagnostics);
    Get.put<AuditRepository>(FakeAuditRepository());
    Get.put<OperationalCredentialStore>(credentials);
    Get.put<ApiClient>(ApiClient(config: Get.find()));
  });

  Future<void> open(
    WidgetTester tester, {
    Size size = TestSizes.expanded,
    Locale? locale,
  }) => pumpScreen(
    tester,
    initialRoute: AppRoutes.projectDiagnostics(projectId),
    getPages: AppRoutes.pages,
    size: size,
    startLocale: locale,
  );

  testWidgets('renders the diagnostics the API returned', (tester) async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(
      projectId: projectId,
      activeAgentRuns: 2,
      staleAgentRuns: 1,
      attemptCount: 4,
      repairAttemptCount: 1,
    );

    await open(tester);

    expect(find.text('Project diagnostics'), findsOneWidget);
    expect(find.text('Summary'), findsOneWidget);
    expect(find.text('Agent runs'), findsOneWidget);
    expect(find.text('Work items by status'), findsOneWidget);
  });

  testWidgets('shows work item counts under their backend status names', (
    tester,
  ) async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(
      projectId: projectId,
      workItemStatusCounts: const <String, int>{'Completed': 5, 'Blocked': 2},
    );

    await open(tester);

    expect(find.text('Completed'), findsWidgets);
    expect(find.text('Blocked'), findsWidgets);
    expect(find.text('5'), findsWidgets);
  });

  testWidgets('states that stale runs are project-scoped', (tester) async {
    await open(tester);

    expect(find.text('Scope of these counts'), findsOneWidget);
    expect(
      find.textContaining('no system-wide stale count'),
      findsOneWidget,
      reason: 'G-STALE-GLOBAL must be stated, not worked around',
    );
  });

  testWidgets('states that acceptance evidence is unavailable', (tester) async {
    await open(tester);

    expect(find.text('Acceptance'), findsWidgets);
    expect(
      find.textContaining('Acceptance results are not exposed'),
      findsOneWidget,
    );
  });

  testWidgets('shows a recorded failure with its category', (tester) async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(
      projectId: projectId,
      lastFailureAtUtc: DateTime.utc(2026, 9, 19, 11),
      lastFailureCategory: FailureCategory.build,
      lastFailureMessage: 'dotnet build failed with 2 errors.',
    );

    await open(tester);

    expect(find.text('dotnet build failed with 2 errors.'), findsOneWidget);
    expect(find.textContaining('Build'), findsWidgets);
  });

  testWidgets('401 offers the key entry instead of a retry', (tester) async {
    diagnostics.projectDiagnosticsError = const AuthenticationError();

    await open(tester);

    expect(find.text('Operator access required'), findsWidgets);
    expect(find.text('Save key'), findsOneWidget);
  });

  testWidgets('entering a key reloads the diagnostics', (tester) async {
    diagnostics.projectDiagnosticsError = const AuthenticationError();
    await open(tester);

    diagnostics.projectDiagnosticsError = null;
    await tester.enterText(find.byType(TextField).last, 'test-key-0001');
    await tester.tap(find.text('Save key'));
    await tester.pumpAndSettle();

    expect(find.text('Summary'), findsOneWidget);
    expect(await credentials.read(), 'test-key-0001');
  });

  testWidgets('404 is its own state, not a server failure', (tester) async {
    diagnostics.projectDiagnosticsError = const NotFoundError();

    await open(tester);

    expect(find.text('No diagnostics for this project'), findsOneWidget);
  });

  for (final (name, size) in <(String, Size)>[
    ('compact', TestSizes.compact),
    ('medium', TestSizes.medium),
    ('expanded', TestSizes.expanded),
  ]) {
    testWidgets('renders without overflow at $name width', (tester) async {
      await open(tester, size: size);

      expect(find.text('Summary'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('renders German without untranslated keys', (tester) async {
    await open(tester, locale: const Locale('de'));

    expect(find.text('Projektdiagnose'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });
}
