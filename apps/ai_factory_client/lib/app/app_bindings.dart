import 'package:flutter/foundation.dart';
import 'package:get/get.dart' hide Trans;

import '../core/config/app_config.dart';
import '../core/errors/error_mapper.dart';
import '../core/localization/locale_service.dart';
import '../core/network/api_client.dart';
import '../core/polling/app_activity.dart';
import '../core/security/operational_credential_store.dart';
import '../core/theme/theme_service.dart';
import '../data/repositories/api_agent_run_repository.dart';
import '../data/repositories/api_approval_repository.dart';
import '../data/repositories/api_audit_repository.dart';
import '../data/repositories/api_operations_repository.dart';
import '../data/repositories/api_project_repository.dart';
import '../data/repositories/api_work_item_repository.dart';
import '../domain/repositories/repositories.dart';

/// Composition root.
///
/// GetX is the dependency-injection container here — it is not the
/// localization engine (that is `easy_localization`) and not the HTTP client
/// (that is Dio behind [ApiClient]).
///
/// [LocaleService] and [ThemeService] are registered before `runApp` so their
/// persisted values are available on the first frame. Everything else is
/// registered lazily, keyed by its **interface**: a controller resolves
/// `Get.find<ProjectRepository>()` and never learns which implementation it
/// received, which is what lets a test swap in a fake.
class AppBindings extends Bindings {
  AppBindings();

  /// Services that must exist before the first frame.
  static Future<void> initPreRunApp() async {
    final localeService = LocaleService();
    await localeService.load();
    Get.put<LocaleService>(localeService, permanent: true);

    final themeService = ThemeService();
    await themeService.load();
    Get.put<ThemeService>(themeService, permanent: true);

    Get.put<AppConfig>(AppConfig.fromEnvironment(), permanent: true);
  }

  @override
  void dependencies() {
    Get.lazyPut<ErrorMapper>(() => const ErrorMapper(), fenix: true);

    // One Dio instance for the whole application: repositories never build
    // their own, so timeouts, headers, interceptors and the operational key
    // are configured in exactly one place.
    Get.lazyPut<ApiClient>(
      () => ApiClient(
        config: Get.find<AppConfig>(),
        errorMapper: Get.find<ErrorMapper>(),
      ),
      fenix: true,
    );

    Get.lazyPut<ProjectRepository>(
      () => ApiProjectRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<WorkItemRepository>(
      () => ApiWorkItemRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<AgentRunRepository>(
      () => ApiAgentRunRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<DiagnosticsRepository>(
      () => ApiDiagnosticsRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<OperationsRepository>(
      () => ApiOperationsRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<ApprovalRepository>(
      () => ApiApprovalRepository(Get.find<ApiClient>()),
      fenix: true,
    );
    Get.lazyPut<AuditRepository>(
      () => ApiAuditRepository(Get.find<ApiClient>()),
      fenix: true,
    );

    // Foreground/background for every polling screen. One listener for the
    // whole application; each coordinator subscribes to its flag.
    if (!Get.isRegistered<AppActivity>()) {
      Get.put<AppActivity>(AppActivity.fromBinding(), permanent: true);
    }

    // The operational key's only home. Registered by its interface so a test
    // substitutes an in-memory store and never touches a real keystore.
    // On the web, memory only — see `credentialStoreFor` — and permanent:
    // a lazily created instance is disposed with the route that first used
    // it, and re-creating a session store would silently empty it.
    if (kIsWeb) {
      Get.put<OperationalCredentialStore>(
        credentialStoreFor(isWeb: true),
        permanent: true,
      );
    } else {
      Get.lazyPut<OperationalCredentialStore>(
        () => credentialStoreFor(isWeb: false),
        fenix: true,
      );
    }
  }
}
