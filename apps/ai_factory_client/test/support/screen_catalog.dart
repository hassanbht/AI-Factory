import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/core/utils/app_clock.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import 'fake_repositories.dart';
import 'test_app.dart';

/// The real application screens, staged with one deterministic data set.
///
/// Golden, contrast, semantics and text-scaling tests all render the same
/// catalog, so a screen is judged on identical content everywhere and a new
/// screen is added in one place. Every value is fixed — ids, names, times —
/// and [pinCatalogClock] pins "now" and the display zone, so nothing in a
/// rendering depends on the machine, the wall clock or the CI runner's zone.
class ScreenCatalog {
  ScreenCatalog._();

  /// "Now" for every catalog rendering. The data below is dated relative to
  /// it, so relative labels read "2 hours ago", never a drifting value.
  static final DateTime now = DateTime.utc(2026, 9, 20, 12);

  static const String implementationId = 'a1b2c3d4-0000-4000-8000-000000000001';
  static const String reviewId = 'a1b2c3d4-0000-4000-8000-000000000002';

  static final CatalogData data = CatalogData._();
}

/// Pins the clock and renders every timestamp in UTC. Undone on tear-down.
void pinCatalogClock() {
  AppClock.pin(now: ScreenCatalog.now, toLocal: (utc) => utc);
  addTearDown(AppClock.reset);
}

/// The fixed data set.
class CatalogData {
  CatalogData._();

  DateTime _ago(Duration d) => ScreenCatalog.now.subtract(d);

  List<Project> projects() => <Project>[
    testProject(
      id: ScreenCatalog.implementationId,
      name: 'Customer API',
      status: ProjectStatus.implementation,
      description:
          'Public REST API for customer accounts, with audit history and '
          'role-based access.',
      gitRepository: 'https://git.example.test/acme/customer-api.git',
      gitBranch: 'main',
      createdAtUtc: _ago(const Duration(days: 1, hours: 2)),
      updatedAtUtc: _ago(const Duration(minutes: 4)),
      lifecycleStartedAtUtc: _ago(const Duration(days: 1)),
    ),
    testProject(
      id: ScreenCatalog.reviewId,
      name: 'Billing Service',
      status: ProjectStatus.humanReview,
      description: 'Invoice generation and payment reconciliation.',
      createdAtUtc: _ago(const Duration(days: 3)),
      updatedAtUtc: _ago(const Duration(hours: 1)),
      lifecycleStartedAtUtc: _ago(const Duration(days: 3)),
    ),
    testProject(
      id: 'a1b2c3d4-0000-4000-8000-000000000003',
      name: 'Inventory Sync',
      status: ProjectStatus.created,
      description: null,
      createdAtUtc: _ago(const Duration(minutes: 20)),
    ),
    testProject(
      id: 'a1b2c3d4-0000-4000-8000-000000000004',
      name: 'Legacy Migration',
      status: ProjectStatus.failed,
      currentError: 'Build failed: 3 tests are failing in OrderMapperTests.',
      createdAtUtc: _ago(const Duration(days: 5)),
      updatedAtUtc: _ago(const Duration(hours: 6)),
      lifecycleStartedAtUtc: _ago(const Duration(days: 5)),
    ),
    testProject(
      id: 'a1b2c3d4-0000-4000-8000-000000000005',
      name: 'Documentation Portal',
      status: ProjectStatus.completed,
      createdAtUtc: _ago(const Duration(days: 12)),
      updatedAtUtc: _ago(const Duration(days: 9)),
      lifecycleStartedAtUtc: _ago(const Duration(days: 12)),
    ),
    testProject(
      id: 'a1b2c3d4-0000-4000-8000-000000000006',
      name: 'Payments Gateway',
      status: ProjectStatus.paused,
      createdAtUtc: _ago(const Duration(days: 2)),
      updatedAtUtc: _ago(const Duration(hours: 20)),
      lifecycleStartedAtUtc: _ago(const Duration(days: 2)),
    ),
  ];

  WorkItem _item(
    String id,
    String projectId,
    String title,
    WorkItemStatus status, {
    WorkItemType type = WorkItemType.implementation,
    int attempts = 0,
  }) => testWorkItem(
    id: id,
    projectId: projectId,
    title: title,
    type: type,
    status: status,
    attemptCount: attempts,
    description: '$title for the customer account endpoints.',
    createdAtUtc: _ago(const Duration(hours: 20)),
    updatedAtUtc: _ago(const Duration(minutes: 30)),
  );

  List<WorkItem> workItems(String projectId, {bool allDone = false}) {
    WorkItemStatus s(WorkItemStatus status) =>
        allDone ? WorkItemStatus.completed : status;
    return <WorkItem>[
      _item(
        'w1-$projectId',
        projectId,
        'Analyze requirements',
        WorkItemStatus.completed,
        type: WorkItemType.analysis,
        attempts: 1,
      ),
      _item(
        'w2-$projectId',
        projectId,
        'Design the database schema',
        WorkItemStatus.completed,
        type: WorkItemType.architecture,
        attempts: 2,
      ),
      _item(
        'w3-$projectId',
        projectId,
        'Implement the account endpoints',
        s(WorkItemStatus.running),
        attempts: 1,
      ),
      _item(
        'w4-$projectId',
        projectId,
        'Write integration tests',
        s(WorkItemStatus.ready),
        type: WorkItemType.test,
      ),
      _item(
        'w5-$projectId',
        projectId,
        'Review the implementation',
        s(WorkItemStatus.draft),
        type: WorkItemType.review,
      ),
    ];
  }

  ProjectPlan plan(String projectId, {bool allDone = false}) {
    final items = workItems(projectId, allDone: allDone);
    TaskReadiness r(TaskReadiness readiness) =>
        allDone ? TaskReadiness.completed : readiness;
    return ProjectPlan(
      projectId: projectId,
      planHash: 'plan-0001',
      nodes: <WorkItemNode>[
        testWorkItemNode(
          workItem: items[0],
          planKey: 'T01',
          readiness: TaskReadiness.completed,
        ),
        testWorkItemNode(
          workItem: items[1],
          planKey: 'T02',
          dependsOnPlanKeys: const ['T01'],
          dependsOnWorkItemIds: [items[0].id],
          readiness: TaskReadiness.completed,
        ),
        testWorkItemNode(
          workItem: items[2],
          planKey: 'T03',
          dependsOnPlanKeys: const ['T02'],
          dependsOnWorkItemIds: [items[1].id],
          readiness: r(TaskReadiness.running),
        ),
        testWorkItemNode(
          workItem: items[3],
          planKey: 'T04',
          dependsOnPlanKeys: const ['T03'],
          dependsOnWorkItemIds: [items[2].id],
          readiness: r(TaskReadiness.waitingForDependency),
        ),
        testWorkItemNode(
          workItem: items[4],
          planKey: 'T05',
          dependsOnPlanKeys: const ['T03', 'T04'],
          dependsOnWorkItemIds: [items[2].id, items[3].id],
          readiness: r(TaskReadiness.waitingForDependency),
        ),
      ],
    );
  }

  List<AgentDefinition> definitions() => <AgentDefinition>[
    testAgentDefinition(id: 'def-impl', name: 'Implementation Agent'),
    testAgentDefinition(
      id: 'def-arch',
      name: 'Architecture Agent',
      agentType: WorkItemType.architecture,
    ),
    testAgentDefinition(
      id: 'def-test',
      name: 'Testing Agent',
      agentType: WorkItemType.test,
    ),
  ];

  List<AgentRun> agentRuns(String projectId, {bool allDone = false}) =>
      <AgentRun>[
        testAgentRun(
          id: 'r3-$projectId',
          projectId: projectId,
          workItemId: 'w3-$projectId',
          agentDefinitionId: 'def-impl',
          status: allDone ? AgentRunStatus.succeeded : AgentRunStatus.running,
          createdAtUtc: _ago(const Duration(minutes: 9)),
          startedAtUtc: _ago(const Duration(minutes: 8)),
          correlationId: 'corr-0003',
        ),
        testAgentRun(
          id: 'r2-$projectId',
          projectId: projectId,
          workItemId: 'w2-$projectId',
          agentDefinitionId: 'def-arch',
          status: AgentRunStatus.succeeded,
          attempt: 2,
          createdAtUtc: _ago(const Duration(hours: 3)),
          startedAtUtc: _ago(const Duration(hours: 3)),
          completedAtUtc: _ago(const Duration(hours: 2, minutes: 40)),
          duration: const Duration(minutes: 20),
          correlationId: 'corr-0002',
          structuredResult: const StructuredAgentResult(
            version: 1,
            status: StructuredResultStatus.succeeded,
            rawStatus: 'Succeeded',
            summary: 'Schema designed with 6 tables and 2 views.',
            warnings: <String>['Index on audit_log.created_at is advised.'],
            errors: <String>[],
            artifacts: <String>['docs/schema.md', 'db/001_initial.sql'],
          ),
        ),
        testAgentRun(
          id: 'r1-$projectId',
          projectId: projectId,
          workItemId: 'w2-$projectId',
          agentDefinitionId: 'def-arch',
          status: AgentRunStatus.failed,
          createdAtUtc: _ago(const Duration(hours: 4)),
          startedAtUtc: _ago(const Duration(hours: 4)),
          completedAtUtc: _ago(const Duration(hours: 3, minutes: 50)),
          duration: const Duration(minutes: 10),
          failureCategory: FailureCategory.model,
          errorMessage: 'The model response did not match the contract.',
          correlationId: 'corr-0001',
        ),
      ];
}

/// One real screen, reachable by its production route.
enum CatalogScreen {
  dashboard,
  projects,
  createProject,
  projectDetail,
  workItems,
  agentRuns,
  humanReview,
  operations,
  diagnostics,
  settings;

  /// Screens whose content runs well below one window; they are rendered on
  /// a taller surface so the capture shows every section.
  bool get isTall => switch (this) {
    projectDetail || workItems || agentRuns || humanReview => true,
    operations || diagnostics => true,
    _ => false,
  };

  String get route => switch (this) {
    dashboard => AppRoutes.dashboard,
    projects || createProject => AppRoutes.projects,
    projectDetail ||
    workItems ||
    agentRuns => AppRoutes.projectDetail(ScreenCatalog.implementationId),
    humanReview => AppRoutes.projectDetail(ScreenCatalog.reviewId),
    operations => AppRoutes.operations,
    diagnostics => AppRoutes.projectDiagnostics(ScreenCatalog.implementationId),
    settings => AppRoutes.settings,
  };
}

/// Registers the catalog repositories for [screen]. Call after
/// [registerTestServices].
void registerCatalog(CatalogScreen screen) {
  final data = ScreenCatalog.data;
  final projectId = screen == CatalogScreen.humanReview
      ? ScreenCatalog.reviewId
      : ScreenCatalog.implementationId;
  final allDone = screen == CatalogScreen.humanReview;

  final projects = FakeProjectRepository(projects: data.projects())
    ..planResult = data.plan(projectId, allDone: allDone);
  final workItems = FakeWorkItemRepository(
    workItems: data.workItems(projectId, allDone: allDone),
  );
  final agentRuns = FakeAgentRunRepository(
    runs: data.agentRuns(projectId, allDone: allDone),
    definitions: data.definitions(),
  );

  Get.put<ProjectRepository>(projects);
  Get.put<WorkItemRepository>(workItems);
  Get.put<AgentRunRepository>(agentRuns);
  Get.put<ApprovalRepository>(FakeApprovalRepository());
  Get.put<OperationsRepository>(FakeOperationsRepository());
  Get.put<DiagnosticsRepository>(
    FakeDiagnosticsRepository()
      ..projectDiagnostics = testProjectDiagnostics(
        projectId: ScreenCatalog.implementationId,
        latestTaskId: 'w3-${ScreenCatalog.implementationId}',
        latestRunId: 'r3-${ScreenCatalog.implementationId}',
        latestRunStatus: 'Running',
        lastFailureAtUtc: ScreenCatalog.now.subtract(const Duration(hours: 4)),
        lastFailureCategory: FailureCategory.model,
        lastFailureMessage: 'The model response did not match the contract.',
      ),
  );
  Get.put<AuditRepository>(
    FakeAuditRepository(
      entries: [
        testAuditLogEntry(
          timestampUtc: ScreenCatalog.now.subtract(const Duration(minutes: 8)),
          projectId: ScreenCatalog.implementationId,
          message:
              'Agent started for work item "Implement the account '
              'endpoints".',
        ),
      ],
    ),
  );
  Get.put<OperationalCredentialStore>(InMemoryOperationalCredentialStore());
  Get.put<ApiClient>(ApiClient(config: Get.find()));
}

/// Renders [screen] and brings it into its captured state: the create dialog
/// open, a work item or an agent run selected.
Future<void> pumpCatalogScreen(
  WidgetTester tester,
  CatalogScreen screen, {
  required Size size,
  Locale locale = const Locale('en'),
  ThemeData? theme,
}) async {
  await pumpScreen(
    tester,
    initialRoute: screen.route,
    getPages: AppRoutes.pages,
    size: size,
    startLocale: locale,
    theme: theme,
  );

  switch (screen) {
    case CatalogScreen.createProject:
      await tester.tap(find.text('projects.action.create'.tr()).first);
      await tester.pumpAndSettle();
    case CatalogScreen.workItems:
      Get.find<WorkItemsController>().selectWorkItem(
        'w3-${ScreenCatalog.implementationId}',
      );
      await tester.pumpAndSettle();
      await _reveal(tester, 'workItems.title');
    case CatalogScreen.agentRuns:
      Get.find<AgentRunsController>().selectAgentRun(
        'r2-${ScreenCatalog.implementationId}',
      );
      await tester.pumpAndSettle();
      await _reveal(tester, 'agentRuns.title');
    case CatalogScreen.humanReview:
      await _reveal(tester, 'humanReview.title');
    default:
      break;
  }
}

/// Scrolls the page's list until the section titled by [key] is on screen,
/// so the capture shows the section itself rather than only the header.
/// The list builds lazily: a section below the fold does not exist yet.
Future<void> _reveal(WidgetTester tester, String key) =>
    revealInPage(tester, find.text(key.tr()));

/// Scrolls the page's main vertical list until [finder] is built and on
/// screen.
Future<void> revealInPage(WidgetTester tester, Finder finder) async {
  if (finder.evaluate().isEmpty) {
    await tester.scrollUntilVisible(
      finder,
      300,
      scrollable: find
          .byWidgetPredicate(
            (w) => w is Scrollable && w.axisDirection == AxisDirection.down,
          )
          .first,
    );
  }
  await tester.ensureVisible(finder.first);
  await tester.pumpAndSettle();
}
