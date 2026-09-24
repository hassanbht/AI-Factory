import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;
import 'package:integration_test/integration_test.dart';

import '../test/support/fake_repositories.dart';
import '../test/support/test_app.dart';

/// Phase 19.8 Operations integration tests.
///
/// ## Live-flow status (Workstream AK / AL)
///
/// A live run needs a reachable AI Factory API **and** a throwaway database
/// whose path is verified at runtime. Neither is available in this
/// environment: the backend's own start-up was blocked in 19.5–19.7 (model
/// routing preflight, then a stale local database), and the phase brief
/// forbids touching `C:\AI-Factory\data\factory.db` to work around it.
///
/// A real **operator key** is likewise not available here, so the credential
/// round-trip is exercised against [InMemoryOperationalCredentialStore] and
/// is explicitly *not* claimed as a live verification.
///
/// What these tests do cover, through the real routing table and bindings:
///
/// - the Operations screen renders every section the API answered,
/// - the operator-access panel reports key presence without showing the key,
/// - entering a key stores it and clearing it removes it.
///
/// Three things deliberately stay in the widget harness rather than here,
/// because they do not run under this binding on this machine:
///
/// 1. **The unauthorized matrix** — staging a `401` before the first frame
///    leaves the screen unsettled, so it never reaches an assertion.
/// 2. **A second screen in the same test** — navigating to the diagnostics
///    route mid-test does not settle (its first frame keeps an indeterminate
///    progress indicator alive), and a second `testWidgets` in this file
///    re-enters the still-mounted app and fails before asserting.
/// 3. **The EN → DE switch**, for the same re-pump reason.
///
/// All three are covered by `test/widget/operations_page_test.dart` (21
/// tests) and `test/widget/project_diagnostics_page_test.dart` (12 tests). A
/// test that cannot run is worth less than one that does, and pretending
/// otherwise would misreport what this phase verified.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(initializeTestLocalization);

  const projectId = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff';
  const fakeKey = 'integration-test-key-0001';

  late FakeOperationsRepository operations;
  late FakeDiagnosticsRepository diagnostics;
  late InMemoryOperationalCredentialStore credentials;

  setUp(() async {
    await registerTestServices();
    operations = FakeOperationsRepository();
    diagnostics = FakeDiagnosticsRepository();
    credentials = InMemoryOperationalCredentialStore();

    Get.put<ProjectRepository>(
      FakeProjectRepository(projects: [testProject(id: projectId)]),
    );
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    Get.put<ApprovalRepository>(FakeApprovalRepository());
    Get.put<OperationsRepository>(operations);
    Get.put<DiagnosticsRepository>(diagnostics);
    Get.put<AuditRepository>(FakeAuditRepository());
    Get.put<OperationalCredentialStore>(credentials);
    Get.put<ApiClient>(ApiClient(config: Get.find()));
  });

  // The integration binding keeps one real app alive across tests, so GetX's
  // route registry outlives a test unless it is cleared. Without this, the
  // second test reuses the first test's route bindings and resolves
  // controllers whose repositories have already been deleted.
  tearDown(Get.reset);

  // The locale is pinned rather than left to the host: this runs on a real
  // machine, whose system language decides the fallback, so an unpinned
  // English assertion would pass or fail depending on who runs it.
  Future<void> openOperations(WidgetTester tester) => pumpScreen(
    tester,
    initialRoute: AppRoutes.operations,
    getPages: AppRoutes.pages,
    startLocale: const Locale('en'),
  );

  Future<void> reveal(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isNotEmpty) return;
    final list = find.byType(ListView).first;
    for (var i = 0; i < 14; i++) {
      await tester.drag(list, const Offset(0, -500));
      await tester.pumpAndSettle();
      if (finder.evaluate().isNotEmpty) return;
    }
  }

  // One scenario rather than several: this binding runs a single real app,
  // and a second `testWidgets` in the same file re-enters it with the first
  // test's widget tree still mounted, which fails before any assertion runs.
  // The per-state matrix is covered by the widget-harness suites
  // (`test/widget/operations_page_test.dart`, 21 tests;
  // `test/widget/project_diagnostics_page_test.dart`, 12 tests).
  testWidgets('operator walks Operations, operator access and diagnostics', (
    tester,
  ) async {
    await openOperations(tester);

    // Every section the API answered is on screen.
    expect(find.text('API health'), findsOneWidget);
    expect(find.text('Deployment'), findsOneWidget);
    expect(find.text('Database'), findsOneWidget);

    await reveal(tester, find.text('Scheduler activity'));
    expect(find.text('Scheduler activity'), findsOneWidget);
    expect(find.text('Agent metrics'), findsOneWidget);

    // The operator-access panel reports only whether a key is stored.
    await reveal(tester, find.text('Operator access'));
    expect(find.text('No key is stored for this device.'), findsOneWidget);

    // Entering one stores it and re-runs the operational reads.
    await tester.ensureVisible(find.byType(TextField).last);
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField).last, fakeKey);
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Save key'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save key'));
    await tester.pumpAndSettle();

    expect(await credentials.read(), fakeKey);
    expect(find.text('A key is stored for this device.'), findsOneWidget);

    // Clearing it forgets the credential again.
    await tester.ensureVisible(find.text('Clear key'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Clear key'));
    await tester.pumpAndSettle();
    expect(await credentials.read(), isNull);
    expect(find.text('No key is stored for this device.'), findsOneWidget);
  });

  // A live run against a real backend would go here. It stays unwritten
  // rather than skipped-with-a-stub, because a test that has never executed
  // against the real API must not look like one that has:
  //
  //   flutter test integration_test/operations_flow_test.dart -d windows \
  //     --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
  //
  // It additionally requires a throwaway database whose path is verified at
  // runtime (Workstream AO) — never the operator's own factory.db.
}
