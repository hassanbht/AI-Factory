import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../app/app_routes.dart';
import '../../core/config/app_config.dart';
import '../../core/errors/app_error.dart';
import '../../core/localization/relative_time.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../core/theme/app_theme.dart';
import '../../domain/models/project.dart';
import '../controllers/dashboard_controller.dart';
import '../models/dashboard_snapshot.dart';
import '../models/project_list_filter.dart';
import '../shell/app_shell.dart';
import '../shell/shell_destination.dart';
import '../widgets/focus_ring.dart';
import '../widgets/live_updates.dart';
import '../widgets/panels.dart';
import '../widgets/state_views.dart';
import '../widgets/status_chip.dart';
import 'projects_page.dart';

/// The operator overview.
///
/// Three bands, in this order and visually separated:
///
/// 1. **Needs attention** — the projects a person has to act on.
/// 2. **Projects** — counts, each one a way into the filtered list.
/// 3. **Platform** — health and scheduler activity, deliberately last and
///    deliberately smaller.
///
/// The separation is the point: a healthy platform is not evidence that a
/// project succeeded, so platform state never shares a band with project
/// state. Every figure here comes from an endpoint this client actually
/// calls; nothing is estimated, and anything the API does not expose says so.
class DashboardPage extends StatelessWidget {
  const DashboardPage({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = Get.find<DashboardController>();

    return LiveUpdatesScope(
      poller: controller.live,
      child: AppShell(
        destination: ShellDestination.dashboard,
        actions: [
          Obx(
            () => IconButton(
              onPressed: controller.state.value.isBusy
                  ? null
                  : controller.refreshData,
              tooltip: 'common.refresh'.tr(),
              icon: const Icon(Icons.refresh),
            ),
          ),
        ],
        builder: (context) => Obx(() {
          final state = controller.state.value;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              LiveStatusIndicator(poller: controller.live),
              const SizedBox(height: AppSpacing.sm),
              // A manual refresh keeps the existing content on screen and shows
              // a thin progress line instead of replacing everything with a
              // spinner. A background poll shows nothing but the indicator.
              if (state is Refreshing<DashboardSnapshot>)
                const LinearProgressIndicator(minHeight: 2),
              Expanded(
                child: AsyncView<DashboardSnapshot>(
                  state: state,
                  onRetry: controller.load,
                  builder: (context, snapshot) => _DashboardBody(
                    snapshot: snapshot,
                    failures: state is Partial<DashboardSnapshot>
                        ? state.failures
                        : const [],
                  ),
                ),
              ),
            ],
          );
        }),
      ),
    );
  }
}

class _DashboardBody extends StatelessWidget {
  const _DashboardBody({required this.snapshot, required this.failures});

  final DashboardSnapshot snapshot;

  /// Sources that failed while the project list succeeded. The affected tiles
  /// already read "Not available"; this names the failure so the operator
  /// knows it was a failure rather than an absent feature.
  final List<AppError> failures;

  @override
  Widget build(BuildContext context) {
    return ListView(
      children: [
        if (failures.isNotEmpty) ...[
          _PartialFailureNotice(failures: failures),
          const SizedBox(height: AppSpacing.md),
        ],
        _NeedsAttentionBand(projects: snapshot.needsAttention),
        const SizedBox(height: AppSpacing.md),
        _ProjectStateBand(counts: snapshot.counts),
        const SizedBox(height: AppSpacing.md),
        _RecentBand(projects: snapshot.recent),
        const SizedBox(height: AppSpacing.lg),
        _PlatformBand(snapshot: snapshot),
        const SizedBox(height: AppSpacing.md),
      ],
    );
  }
}

/// Some, but not all, of the Dashboard's sources answered.
class _PartialFailureNotice extends StatelessWidget {
  const _PartialFailureNotice({required this.failures});

  final List<AppError> failures;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(AppSpacing.md),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(AppRadius.md),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          UnavailableNote(
            title: 'dashboard.partial.title'.tr(),
            reason: 'dashboard.partial.message'.tr(),
          ),
          for (final failure in failures) ...[
            const SizedBox(height: AppSpacing.xs),
            Text(
              failure.messageKey.tr(),
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// Band 1 — what a person has to look at.
class _NeedsAttentionBand extends StatelessWidget {
  const _NeedsAttentionBand({required this.projects});

  final List<Project> projects;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return SectionCard(
      title: 'dashboard.needsAttention.title'.tr(),
      subtitle: 'dashboard.needsAttention.subtitle'.tr(),
      child: projects.isEmpty
          ? Row(
              children: [
                Icon(
                  Icons.check_circle_outline,
                  size: 18,
                  color: StatusPalette.success(theme.colorScheme),
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    'dashboard.needsAttention.empty'.tr(),
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
              ],
            )
          : Column(
              children: [
                for (final project in projects)
                  _CompactProjectRow(project: project),
              ],
            ),
    );
  }
}

/// Band 2 — counts, each a filtered view of the same list.
class _ProjectStateBand extends StatelessWidget {
  const _ProjectStateBand({required this.counts});

  final ProjectStatusCounts counts;

  void _open(ProjectStatusFilter filter) {
    Get.offNamed<void>(
      AppRoutes.projects,
      arguments: ProjectsPageArguments(statusFilter: filter),
    );
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return SectionCard(
      title: 'dashboard.projects.title'.tr(),
      // States plainly that the client did the counting, so the figure is
      // never mistaken for something the backend reports.
      subtitle: 'dashboard.projects.subtitle'.tr(),
      child: ResponsiveTileGrid(
        children: [
          MetricTile(
            label: 'dashboard.projects.total'.tr(),
            value: '${counts.total}',
            icon: Icons.folder_outlined,
            onTap: () => _open(ProjectStatusFilter.all),
          ),
          MetricTile(
            label: 'dashboard.projects.active'.tr(),
            value: '${counts.active}',
            icon: Icons.autorenew,
            color: StatusPalette.active(scheme),
            onTap: () => _open(ProjectStatusFilter.active),
          ),
          MetricTile(
            label: 'dashboard.projects.humanReview'.tr(),
            value: '${counts.humanReview}',
            icon: Icons.how_to_reg_outlined,
            color: StatusPalette.attention(scheme),
            onTap: () => _open(ProjectStatusFilter.humanReview),
          ),
          MetricTile(
            label: 'dashboard.projects.notStarted'.tr(),
            value: '${counts.notStarted}',
            icon: Icons.schedule_outlined,
            onTap: () => _open(ProjectStatusFilter.notStarted),
          ),
          MetricTile(
            label: 'dashboard.projects.paused'.tr(),
            value: '${counts.paused}',
            icon: Icons.pause_circle_outline,
            color: StatusPalette.warning(scheme),
            onTap: () => _open(ProjectStatusFilter.paused),
          ),
          MetricTile(
            label: 'dashboard.projects.completed'.tr(),
            value: '${counts.completed}',
            icon: Icons.check_circle_outline,
            color: StatusPalette.success(scheme),
            onTap: () => _open(ProjectStatusFilter.completed),
          ),
          MetricTile(
            label: 'dashboard.projects.failed'.tr(),
            value: '${counts.failed}',
            icon: Icons.error_outline,
            color: StatusPalette.danger(scheme),
            onTap: () => _open(ProjectStatusFilter.failed),
          ),
          MetricTile(
            label: 'dashboard.projects.cancelled'.tr(),
            value: '${counts.cancelled}',
            icon: Icons.cancel_outlined,
            onTap: () => _open(ProjectStatusFilter.failed),
          ),
        ],
      ),
    );
  }
}

class _RecentBand extends StatelessWidget {
  const _RecentBand({required this.projects});

  final List<Project> projects;

  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: 'dashboard.recent.title'.tr(),
      trailing: TextButton(
        onPressed: () => Get.offNamed<void>(AppRoutes.projects),
        child: Text('dashboard.recent.viewAll'.tr()),
      ),
      child: projects.isEmpty
          ? Text(
              'dashboard.recent.empty'.tr(),
              style: Theme.of(context).textTheme.bodyMedium,
            )
          : Column(
              children: [
                for (final project in projects)
                  _CompactProjectRow(project: project),
              ],
            ),
    );
  }
}

/// Band 3 — the platform itself. Smaller tiles, last position, its own
/// heading: this is not project state and must not read as project state.
class _PlatformBand extends StatelessWidget {
  const _PlatformBand({required this.snapshot});

  final DashboardSnapshot snapshot;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final health = snapshot.health;
    final scheduler = snapshot.scheduler;
    final metrics = snapshot.agentMetrics;
    final locale = context.locale.toString();

    return SectionCard(
      title: 'dashboard.platform.title'.tr(),
      subtitle: 'dashboard.platform.subtitle'.tr(),
      dense: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ResponsiveTileGrid(
            children: [
              _PlatformTile(
                label: 'dashboard.platform.api'.tr(),
                value: health == null
                    ? 'common.notAvailable'.tr()
                    : health.isAlive
                    ? 'dashboard.platform.reachable'.tr()
                    : 'dashboard.platform.unreachable'.tr(),
                tone: health == null
                    ? StatusTone.neutral
                    : health.isAlive
                    ? StatusTone.success
                    : StatusTone.danger,
              ),
              _PlatformTile(
                label: 'dashboard.platform.readiness'.tr(),
                value: health == null
                    ? 'common.notAvailable'.tr()
                    : health.isReady
                    ? 'dashboard.platform.ready'.tr()
                    : 'dashboard.platform.notReady'.tr(),
                tone: health == null
                    ? StatusTone.neutral
                    : health.isReady
                    ? StatusTone.success
                    : StatusTone.warning,
              ),
              _PlatformTile(
                label: 'dashboard.platform.scheduler'.tr(),
                value: scheduler == null
                    ? 'common.notAvailable'.tr()
                    : scheduler.enabled
                    ? 'dashboard.platform.enabled'.tr()
                    : 'dashboard.platform.disabled'.tr(),
                tone: scheduler == null
                    ? StatusTone.neutral
                    : scheduler.enabled
                    ? StatusTone.success
                    : StatusTone.warning,
                detail: scheduler == null
                    ? null
                    : scheduler.hasRunACycle
                    ? 'dashboard.platform.lastCycle'.tr(
                        args: [
                          RelativeTime.format(
                            scheduler.lastCycleAtUtc!,
                            locale,
                          ),
                        ],
                      )
                    : 'dashboard.platform.noCycleYet'.tr(),
              ),
              _PlatformTile(
                label: 'dashboard.platform.agentRuns'.tr(),
                value: metrics == null
                    ? 'common.notAvailable'.tr()
                    : '${metrics.inFlight}',
                tone: StatusTone.neutral,
                detail: metrics == null
                    ? null
                    : 'dashboard.platform.agentRunsDetail'.tr(
                        args: ['${metrics.queued}', '${metrics.running}'],
                      ),
              ),
            ],
          ),
          if (snapshot.agentMetricsRequiresKey) ...[
            const SizedBox(height: AppSpacing.md),
            UnavailableNote(
              title: 'dashboard.platform.agentRuns'.tr(),
              reason: 'dashboard.platform.operationalKeyRequired'.tr(),
            ),
          ],
          // Worker health itself is not exposed by the API (gap
          // `G-WORKER-HEALTH`); scheduler activity is the nearest available
          // signal and is labelled as activity, never as health.
          const SizedBox(height: AppSpacing.md),
          UnavailableNote(
            title: 'dashboard.platform.workerHealth'.tr(),
            reason: 'dashboard.platform.workerHealthNote'.tr(),
          ),
          const SizedBox(height: AppSpacing.md),
          Text(
            'dashboard.platform.versions'.tr(
              args: [AppConfig.clientVersion, AppConfig.targetBackendVersion],
            ),
            style: theme.textTheme.bodySmall?.copyWith(
              color: scheme.onSurfaceVariant,
            ),
          ),
        ],
      ),
    );
  }
}

class _PlatformTile extends StatelessWidget {
  const _PlatformTile({
    required this.label,
    required this.value,
    required this.tone,
    this.detail,
  });

  final String label;
  final String value;
  final StatusTone tone;
  final String? detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = tone.color(theme.colorScheme);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              label,
              style: theme.textTheme.labelMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
            const SizedBox(height: AppSpacing.sm),
            Row(
              children: [
                Icon(tone.icon, size: 16, color: color),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: Text(
                    value,
                    style: theme.textTheme.titleSmall?.copyWith(color: color),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
            if (detail != null) ...[
              const SizedBox(height: AppSpacing.xs),
              Text(
                detail!,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// A project as it appears inside a Dashboard band: identity, state, and a
/// way through to it. Deliberately lighter than the Projects list row — the
/// Dashboard points at projects, it does not manage them.
class _CompactProjectRow extends StatelessWidget {
  const _CompactProjectRow({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = context.locale.toString();

    return FocusRing(
      borderRadius: BorderRadius.circular(AppRadius.sm),
      child: InkWell(
        onTap: () => Get.toNamed<void>(AppRoutes.projectDetail(project.id)),
        borderRadius: BorderRadius.circular(AppRadius.sm),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: AppSpacing.sm),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      project.name,
                      style: theme.textTheme.bodyMedium,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    Text(
                      RelativeTime.format(project.updatedAtUtc, locale),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: AppSpacing.sm),
              StatusChip.project(
                project.status,
                rawStatus: project.rawStatus,
                compact: true,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
