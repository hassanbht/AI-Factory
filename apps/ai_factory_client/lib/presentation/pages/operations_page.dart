import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../controllers/audit_controller.dart';
import '../controllers/operations_controller.dart';
import '../controllers/operator_access_controller.dart';
import '../shell/app_shell.dart';
import '../shell/shell_destination.dart';
import '../widgets/audit_log_section.dart';
import '../widgets/live_updates.dart';
import '../widgets/operations_panels.dart';
import '../widgets/operator_access_panel.dart';

/// The operator's view of the running system.
///
/// Every section is an independent endpoint with an independent state, so the
/// screen stays useful when some of them are unreachable: one `401` on agent
/// metrics leaves health, deployment, database and scheduler exactly where
/// they were (Workstream Z).
///
/// The screen shows only what the API actually returns. Worker health, global
/// stale-run counts and model routing are exposed by no endpoint, so they
/// appear as explicit "not available" notes inside the sections they would
/// belong to — never as a guessed status.
class OperationsPage extends StatelessWidget {
  const OperationsPage({super.key});

  @override
  Widget build(BuildContext context) {
    return LiveUpdatesScope(
      poller: Get.find<OperationsController>().live,
      child: AppShell(
        destination: ShellDestination.operations,
        builder: (context) => const _OperationsBody(),
      ),
    );
  }
}

class _OperationsBody extends StatefulWidget {
  const _OperationsBody();

  @override
  State<_OperationsBody> createState() => _OperationsBodyState();
}

class _OperationsBodyState extends State<_OperationsBody> {
  /// The operator-access panel sits at the bottom of the page, so a `401`
  /// several sections above needs a way to reach it that does not rely on
  /// the operator scrolling to find it.
  final GlobalKey _accessKey = GlobalKey();

  void _revealAccess() {
    final target = _accessKey.currentContext;
    if (target == null) return;
    Scrollable.ensureVisible(
      target,
      duration: const Duration(milliseconds: 250),
    );
  }

  @override
  Widget build(BuildContext context) {
    // Registers the locale dependency so the whole screen re-reads its
    // strings on a language switch (`docs/flutter-architecture.md` §9.5).
    final _ = context.locale;

    final controller = Get.find<OperationsController>();
    final access = Get.find<OperatorAccessController>();
    final audit = Get.find<AuditController>();

    void refreshEverything() {
      controller.refreshData();
      audit.refreshData();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'operations.title'.tr(),
                style: Theme.of(context).textTheme.titleLarge,
              ),
            ),
            Obx(
              () => IconButton(
                onPressed: controller.health.value.isBusy
                    ? null
                    : refreshEverything,
                tooltip: 'operations.refresh'.tr(),
                icon: const Icon(Icons.refresh),
              ),
            ),
          ],
        ),
        // Health, scheduler and metrics refresh on their own; deployment,
        // database and configuration only when asked.
        LiveStatusIndicator(poller: controller.live, onEnterKey: _revealAccess),
        const SizedBox(height: AppSpacing.md),
        Expanded(
          child: ListView(
            children: [
              ResponsiveBuilder(
                builder: (context, sizeClass) {
                  final health = Obx(
                    () => OperationsSection(
                      title: 'operations.health.title'.tr(),
                      subtitle: 'operations.health.subtitle'.tr(),
                      state: controller.health.value,
                      onRetry: controller.retryHealth,
                      onEnterKey: _revealAccess,
                      builder: (context, data) =>
                          SystemHealthPanel(health: data),
                    ),
                  );
                  final deployment = Obx(
                    () => OperationsSection(
                      title: 'deployment.title'.tr(),
                      state: controller.deployment.value,
                      onRetry: controller.retryDeployment,
                      onEnterKey: _revealAccess,
                      builder: (context, data) =>
                          DeploymentInfoPanel(deployment: data),
                    ),
                  );
                  final database = Obx(
                    () => OperationsSection(
                      title: 'database.title'.tr(),
                      state: controller.database.value,
                      onRetry: controller.retryDatabase,
                      onEnterKey: _revealAccess,
                      builder: (context, data) =>
                          DatabaseInfoPanel(database: data),
                    ),
                  );

                  if (sizeClass.isCompact) {
                    return Column(
                      children: [
                        health,
                        const SizedBox(height: AppSpacing.md),
                        deployment,
                        const SizedBox(height: AppSpacing.md),
                        database,
                      ],
                    );
                  }
                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(child: health),
                      const SizedBox(width: AppSpacing.md),
                      Expanded(child: deployment),
                      const SizedBox(width: AppSpacing.md),
                      Expanded(child: database),
                    ],
                  );
                },
              ),
              const SizedBox(height: AppSpacing.md),
              ResponsiveBuilder(
                builder: (context, sizeClass) {
                  final scheduler = Obx(
                    () => OperationsSection(
                      title: 'scheduler.title'.tr(),
                      subtitle: 'scheduler.subtitle'.tr(),
                      state: controller.scheduler.value,
                      onRetry: controller.retryScheduler,
                      onEnterKey: _revealAccess,
                      builder: (context, data) =>
                          SchedulerActivityPanel(scheduler: data),
                    ),
                  );
                  final metrics = Obx(
                    () => OperationsSection(
                      title: 'metrics.title'.tr(),
                      state: controller.metrics.value,
                      onRetry: controller.retryMetrics,
                      onEnterKey: _revealAccess,
                      builder: (context, data) =>
                          AgentMetricsPanel(metrics: data),
                    ),
                  );

                  if (sizeClass.isCompact) {
                    return Column(
                      children: [
                        scheduler,
                        const SizedBox(height: AppSpacing.md),
                        metrics,
                      ],
                    );
                  }
                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(child: scheduler),
                      const SizedBox(width: AppSpacing.md),
                      Expanded(child: metrics),
                    ],
                  );
                },
              ),
              const SizedBox(height: AppSpacing.md),
              Obx(
                () => OperationsSection(
                  title: 'configuration.title'.tr(),
                  subtitle: 'configuration.subtitle'.tr(),
                  state: controller.configuration.value,
                  onRetry: controller.retryConfiguration,
                  onEnterKey: _revealAccess,
                  builder: (context, data) =>
                      ConfigurationInfoPanel(configuration: data),
                ),
              ),
              const SizedBox(height: AppSpacing.md),
              AuditLogSection(controller: audit, onEnterKey: _revealAccess),
              const SizedBox(height: AppSpacing.md),
              KeyedSubtree(
                key: _accessKey,
                child: OperatorAccessPanel(
                  controller: access,
                  // A new key is only useful once the reads that failed are
                  // tried again, so saving one re-runs them.
                  onSaved: refreshEverything,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
