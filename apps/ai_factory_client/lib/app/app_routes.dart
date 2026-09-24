import 'package:get/get.dart' hide Trans;

import '../presentation/controllers/agent_runs_controller.dart';
import '../presentation/controllers/audit_controller.dart';
import '../presentation/controllers/dashboard_controller.dart';
import '../presentation/controllers/diagnostics_controller.dart';
import '../presentation/controllers/human_review_controller.dart';
import '../presentation/controllers/operations_controller.dart';
import '../presentation/controllers/operator_access_controller.dart';
import '../presentation/controllers/project_detail_controller.dart';
import '../presentation/controllers/projects_controller.dart';
import '../presentation/controllers/work_items_controller.dart';
import '../presentation/pages/dashboard_page.dart';
import '../presentation/pages/operations_page.dart';
import '../presentation/pages/project_detail_page.dart';
import '../presentation/pages/project_diagnostics_page.dart';
import '../presentation/pages/projects_page.dart';
import '../presentation/pages/settings_page.dart';

/// Route names. Later phases add nested routes beneath `/projects`
/// (`/projects/:projectId/workitems/:workItemId/runs/:runId`) so project
/// context survives navigation and deep links keep working.
class AppRoutes {
  const AppRoutes._();

  static const String dashboard = '/dashboard';
  static const String projects = '/projects';
  static const String operations = '/operations';
  static const String settings = '/settings';

  /// The pattern GetX matches; [projectDetail] builds a concrete path.
  static const String projectDetailPattern = '/projects/:projectId';

  static String projectDetail(String projectId) => '/projects/$projectId';

  /// Operational diagnostics for one project. Nested under the project so the
  /// id travels in the route and the screen can be deep-linked; the page
  /// re-reads the diagnostics itself rather than receiving them.
  static const String projectDiagnosticsPattern =
      '/projects/:projectId/diagnostics';

  static String projectDiagnostics(String projectId) =>
      '/projects/$projectId/diagnostics';

  static const String initial = dashboard;

  /// Screen controllers are bound **per route**, not globally: GetX disposes
  /// them when the route is left, and each one cancels its in-flight requests
  /// in `onClose`. Repositories stay in the global container, so a controller
  /// resolves the same instance every time.
  static final List<GetPage<dynamic>> pages = <GetPage<dynamic>>[
    GetPage<void>(
      name: dashboard,
      page: DashboardPage.new,
      binding: BindingsBuilder(() {
        Get.lazyPut<DashboardController>(DashboardController.new);
      }),
    ),
    GetPage<void>(
      name: projects,
      page: ProjectsPage.new,
      binding: BindingsBuilder(() {
        Get.lazyPut<ProjectsController>(ProjectsController.new);
      }),
    ),
    GetPage<void>(
      name: projectDetailPattern,
      page: ProjectDetailPage.new,
      binding: BindingsBuilder(() {
        // Given its two sibling sections so the whole screen refreshes in one
        // coordinated polling cycle rather than on three timers.
        Get.lazyPut<ProjectDetailController>(
          () => ProjectDetailController(
            projectId: Get.parameters['projectId'] ?? '',
            workItems: Get.find<WorkItemsController>(),
            agentRuns: Get.find<AgentRunsController>(),
          ),
        );
        Get.lazyPut<WorkItemsController>(
          () =>
              WorkItemsController(projectId: Get.parameters['projectId'] ?? ''),
        );
        Get.lazyPut<AgentRunsController>(
          () =>
              AgentRunsController(projectId: Get.parameters['projectId'] ?? ''),
        );
        // Phase 19.7 — approval mutation controller.
        // Registered after ProjectDetailController so Get.find<ProjectDetailController>()
        // in HumanReviewController's constructor resolves without error.
        Get.lazyPut<HumanReviewController>(
          () => HumanReviewController(
            projectId: Get.parameters['projectId'] ?? '',
          ),
        );
      }),
    ),
    GetPage<void>(
      name: projectDiagnosticsPattern,
      page: ProjectDiagnosticsPage.new,
      binding: BindingsBuilder(() {
        Get.lazyPut<DiagnosticsController>(
          () => DiagnosticsController(
            projectId: Get.parameters['projectId'] ?? '',
          ),
        );
        // Shared with Operations: there is one operational credential, not
        // one per screen, so a key entered here applies there too.
        Get.lazyPut<OperatorAccessController>(OperatorAccessController.new);
      }),
    ),
    GetPage<void>(
      name: operations,
      page: OperationsPage.new,
      binding: BindingsBuilder(() {
        // Registered before OperationsController, whose constructor resolves
        // it for the single post-401 credential retry.
        Get.lazyPut<OperatorAccessController>(OperatorAccessController.new);
        Get.lazyPut<OperationsController>(OperationsController.new);
        Get.lazyPut<AuditController>(AuditController.new);
      }),
    ),
    GetPage<void>(name: settings, page: SettingsPage.new),
  ];
}
