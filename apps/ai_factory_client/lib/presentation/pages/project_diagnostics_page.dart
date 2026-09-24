import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../app/app_routes.dart';
import '../../core/localization/enum_labels.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/models/operations.dart';
import '../controllers/diagnostics_controller.dart';
import '../controllers/operator_access_controller.dart';
import '../widgets/error_panel.dart';
import '../widgets/live_updates.dart';
import '../widgets/operator_access_panel.dart';
import '../widgets/panels.dart';
import '../widgets/state_views.dart';
import '../widgets/timestamp_text.dart';

/// One project's operational diagnostics.
///
/// A separate route and a separate controller from Project Detail: opening
/// this screen re-reads the authoritative diagnostics by `projectId` rather
/// than being handed a snapshot through navigation, and refreshing it does
/// not refresh Project Detail (Workstream AB).
///
/// Stale agent runs are reported **per project**, because that is the only
/// scope the API offers — no total is summed across projects
/// (`G-STALE-GLOBAL`).
class ProjectDiagnosticsPage extends StatelessWidget {
  const ProjectDiagnosticsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final _ = context.locale;
    final controller = Get.find<DiagnosticsController>();

    return LiveUpdatesScope(
      poller: controller.live,
      child: Scaffold(
        appBar: AppBar(
          leading: BackButton(onPressed: () => _back(controller.projectId)),
          title: Text('diagnostics.title'.tr()),
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
        ),
        body: SafeArea(
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(
                maxWidth: AppSpacing.maxContentWidth,
              ),
              child: Padding(
                padding: const EdgeInsets.all(AppSpacing.pageGutter),
                child: Obx(() {
                  final state = controller.state.value;

                  // A `401` is an operator action, not an outage: the screen
                  // offers the key entry instead of a retry that cannot work.
                  if (controller.needsOperatorAccess) {
                    return ListView(
                      children: [
                        LiveStatusIndicator(poller: controller.live),
                        const SizedBox(height: AppSpacing.sm),
                        SectionCard(
                          title: 'operatorAccess.required'.tr(),
                          child: Text(
                            'diagnostics.unauthorized'.tr(),
                            style: Theme.of(context).textTheme.bodyMedium,
                          ),
                        ),
                        const SizedBox(height: AppSpacing.md),
                        OperatorAccessPanel(
                          controller: Get.find<OperatorAccessController>(),
                          onSaved: controller.load,
                        ),
                      ],
                    );
                  }

                  if (controller.isNotFound) {
                    return NotFoundView(
                      title: 'diagnostics.notFound.title'.tr(),
                      message: 'diagnostics.notFound.message'.tr(),
                      actionLabel: 'diagnostics.backToProject'.tr(),
                      onAction: () => _back(controller.projectId),
                    );
                  }

                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      LiveStatusIndicator(poller: controller.live),
                      const SizedBox(height: AppSpacing.sm),
                      Expanded(
                        child: AsyncView<ProjectDiagnostics>(
                          state: state,
                          onRetry: controller.load,
                          builder: (context, data) =>
                              _DiagnosticsBody(data: data),
                        ),
                      ),
                    ],
                  );
                }),
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Back to the project this belongs to, whether the screen was pushed or
  /// opened directly by URL.
  static void _back(String projectId) {
    if (Get.key.currentState?.canPop() ?? false) {
      Get.back<void>();
      return;
    }
    Get.offNamed<void>(AppRoutes.projectDetail(projectId));
  }
}

class _DiagnosticsBody extends StatelessWidget {
  const _DiagnosticsBody({required this.data});

  final ProjectDiagnostics data;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final duration = data.executionDuration;

    final summary = SectionCard(
      title: 'diagnostics.summary'.tr(),
      child: FactList(
        facts: <Fact>[
          Fact(
            label: 'diagnostics.status'.tr(),
            child: Text(
              EnumLabels.projectStatus(
                data.projectStatus,
                rawValue: data.rawProjectStatus,
              ),
              style: theme.textTheme.bodyMedium,
            ),
          ),
          Fact(
            label: 'diagnostics.executionDuration'.tr(),
            child: duration == null
                ? Text(
                    'diagnostics.noDuration'.tr(),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  )
                : Text(
                    'common.durationHms'.tr(
                      args: [
                        '${duration.inHours}',
                        '${duration.inMinutes.remainder(60)}',
                        '${duration.inSeconds.remainder(60)}',
                      ],
                    ),
                    style: theme.textTheme.bodyMedium,
                  ),
          ),
          Fact(
            label: 'diagnostics.attempts'.tr(),
            child: Text(
              '${data.attemptCount}',
              style: theme.textTheme.bodyMedium,
            ),
          ),
          Fact(
            label: 'diagnostics.repairAttempts'.tr(),
            child: Text(
              '${data.repairAttemptCount}',
              style: theme.textTheme.bodyMedium,
            ),
          ),
          if (data.latestTaskId != null)
            Fact(
              label: 'diagnostics.latestTask'.tr(),
              child: SelectableText(
                data.latestTaskStatus == null
                    ? data.latestTaskId!
                    : '${data.latestTaskId!} · ${data.latestTaskStatus!}',
                style: theme.textTheme.bodySmall,
              ),
            ),
          if (data.latestRunId != null)
            Fact(
              label: 'diagnostics.latestRun'.tr(),
              child: SelectableText(
                data.latestRunStatus == null
                    ? data.latestRunId!
                    : '${data.latestRunId!} · ${data.latestRunStatus!}',
                style: theme.textTheme.bodySmall,
              ),
            ),
        ],
      ),
    );

    final runs = SectionCard(
      title: 'diagnostics.agentRuns'.tr(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          FactList(
            facts: <Fact>[
              Fact(
                label: 'diagnostics.activeRuns'.tr(),
                child: Semantics(
                  label:
                      // "Agent runs, Active: 1" — the count means nothing without
                      // the group it belongs to.
                      '${'diagnostics.agentRuns'.tr()}, '
                      '${'diagnostics.activeRuns'.tr()}: ${data.activeAgentRuns}',
                  excludeSemantics: true,
                  child: Text(
                    '${data.activeAgentRuns}',
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
              ),
              Fact(
                label: 'diagnostics.staleRuns'.tr(),
                child: Semantics(
                  label:
                      // "Agent runs, Active: 1" — the count means nothing without
                      // the group it belongs to.
                      '${'diagnostics.agentRuns'.tr()}, '
                      '${'diagnostics.staleRuns'.tr()}: ${data.staleAgentRuns}',
                  excludeSemantics: true,
                  child: Text(
                    '${data.staleAgentRuns}',
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: data.staleAgentRuns > 0
                          ? theme.colorScheme.error
                          : null,
                    ),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.md),
          UnavailableNote(
            title: 'diagnostics.staleScope'.tr(),
            reason: 'diagnostics.staleScopeReason'.tr(),
          ),
        ],
      ),
    );

    return ListView(
      children: [
        ResponsiveBuilder(
          builder: (context, sizeClass) => sizeClass.isCompact
              ? Column(
                  children: [
                    summary,
                    const SizedBox(height: AppSpacing.md),
                    runs,
                  ],
                )
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(child: summary),
                    const SizedBox(width: AppSpacing.md),
                    Expanded(child: runs),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.md),
        _WorkItemCounts(counts: data.workItemStatusCounts),
        if (data.hasRecentFailure) ...[
          const SizedBox(height: AppSpacing.md),
          _RecentFailure(data: data),
        ],
        const SizedBox(height: AppSpacing.md),
        SectionCard(
          title: 'diagnostics.acceptance'.tr(),
          child: UnavailableNote(
            reason: 'diagnostics.acceptanceUnavailable'.tr(),
          ),
        ),
      ],
    );
  }
}

/// Work items by status, keyed by the raw backend status string.
///
/// The counts are shown under the status names the API used. A status this
/// client does not recognize still appears, with its wire spelling.
class _WorkItemCounts extends StatelessWidget {
  const _WorkItemCounts({required this.counts});

  final Map<String, int> counts;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final entries = counts.entries.toList()
      ..sort((a, b) => a.key.compareTo(b.key));

    return SectionCard(
      title: 'diagnostics.workItems'.tr(),
      subtitle: 'diagnostics.workItemsTotal'.tr(
        args: ['${counts.values.fold<int>(0, (sum, c) => sum + c)}'],
      ),
      child: entries.isEmpty
          ? Text(
              'diagnostics.noWorkItems'.tr(),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            )
          : Wrap(
              spacing: AppSpacing.md,
              runSpacing: AppSpacing.sm,
              children: [
                for (final entry in entries)
                  Semantics(
                    label: '${entry.key}: ${entry.value}',
                    excludeSemantics: true,
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          '${entry.value}',
                          style: theme.textTheme.titleMedium,
                        ),
                        Text(
                          entry.key,
                          style: theme.textTheme.labelSmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
    );
  }
}

class _RecentFailure extends StatelessWidget {
  const _RecentFailure({required this.data});

  final ProjectDiagnostics data;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final category = data.lastFailureCategory;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        RecordedErrorPanel(
          title: 'diagnostics.lastFailure'.tr(),
          // The server's own message, rendered as it arrived.
          message:
              data.lastFailureMessage ?? 'diagnostics.noFailureMessage'.tr(),
          footnote: category == null
              ? null
              : 'diagnostics.failureCategory'.tr(
                  args: [EnumLabels.failureCategory(category)],
                ),
        ),
        const SizedBox(height: AppSpacing.sm),
        Row(
          children: [
            Text(
              'diagnostics.lastFailureAt'.tr(),
              style: theme.textTheme.labelSmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(width: AppSpacing.xs),
            TimestampText(
              timestamp: data.lastFailureAtUtc,
              style: theme.textTheme.labelSmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ],
    );
  }
}
