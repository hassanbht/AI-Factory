import 'package:get/get.dart' hide Trans;

import '../presentation/controllers/dashboard_controller.dart';
import '../presentation/controllers/project_detail_controller.dart';
import '../presentation/controllers/projects_controller.dart';
import '../presentation/pages/dashboard_page.dart';
import '../presentation/pages/operations_page.dart';
import '../presentation/pages/project_detail_page.dart';
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
        Get.lazyPut<ProjectDetailController>(
          () => ProjectDetailController(
            projectId: Get.parameters['projectId'] ?? '',
          ),
        );
      }),
    ),
    GetPage<void>(name: operations, page: OperationsPage.new),
    GetPage<void>(name: settings, page: SettingsPage.new),
  ];
}
