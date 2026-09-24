import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/localization/enum_labels.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../controllers/agent_runs_controller.dart';
import '../models/agent_runs_data.dart';
import 'agent_run_detail_panel.dart';
import 'agent_run_tile.dart';
import 'panels.dart';
import 'state_views.dart';

/// Agent Runs and their detail, together — Project Detail's execution layer
/// (Phase 19.6). Selecting a row drives the detail panel, exactly as
/// `WorkItemsSection` does for work items (`docs/flutter-ui-patterns.md`
/// §18); a status filter re-queries the server (a real filter parameter),
/// while a work-item filter narrows the already-loaded page client-side
/// (gap `G-WORKITEM-EVENTS` has no server-side equivalent).
class AgentRunsSection extends StatelessWidget {
  const AgentRunsSection({
    required this.controller,
    this.workItemTitleById = const <String, String>{},
    this.onViewWorkItem,
    super.key,
  });

  final AgentRunsController controller;

  /// Title lookup for the selected run's `workItemId`, resolved by the
  /// caller from the WorkItems section's own data — this widget never reads
  /// `WorkItemRepository` itself.
  final Map<String, String> workItemTitleById;

  /// Navigates to the related work item. Null hides the action.
  final ValueChanged<String>? onViewWorkItem;

  @override
  Widget build(BuildContext context) {
    return Obx(() {
      final state = controller.state.value;
      return AsyncView<AgentRunsData>(
        state: state,
        onRetry: controller.load,
        emptyTitle: 'agentRuns.empty.title'.tr(),
        emptyMessage: 'agentRuns.empty.message'.tr(),
        builder: (context, data) => _AgentRunsBody(
          controller: controller,
          data: data,
          workItemTitleById: workItemTitleById,
          onViewWorkItem: onViewWorkItem,
        ),
      );
    });
  }
}

class _AgentRunsBody extends StatelessWidget {
  const _AgentRunsBody({
    required this.controller,
    required this.data,
    required this.workItemTitleById,
    required this.onViewWorkItem,
  });

  final AgentRunsController controller;
  final AgentRunsData data;
  final Map<String, String> workItemTitleById;
  final ValueChanged<String>? onViewWorkItem;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    final list = SectionCard(
      title: 'agentRuns.title'.tr(),
      subtitle: 'agentRuns.summary'.plural(data.runs.length),
      trailing: IconButton(
        onPressed: controller.refreshData,
        tooltip: 'agentRuns.refresh'.tr(),
        icon: const Icon(Icons.refresh),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _StatusFilterRow(controller: controller),
          const SizedBox(height: AppSpacing.sm),
          Obx(
            () => _WorkItemFilterBanner(
              workItemId: controller.workItemFilter.value,
              workItemTitleById: workItemTitleById,
              onClear: () => controller.filterByWorkItem(null),
            ),
          ),
          Obx(
            () => _AgentRunList(
              data: data,
              selectedId: controller.selectedAgentRunId.value,
              workItemFilter: controller.workItemFilter.value,
              workItemTitleById: workItemTitleById,
              onSelect: controller.selectAgentRun,
            ),
          ),
        ],
      ),
    );

    final detail = Obx(() {
      final selectedId = controller.selectedAgentRunId.value;
      final run = selectedId == null
          ? null
          : data.runs.where((r) => r.id == selectedId).firstOrNull;
      if (run == null) {
        return SectionCard(
          title: 'agentRun.detail.title'.tr(),
          child: Text(
            'agentRun.detail.none'.tr(),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        );
      }

      final definition = data.definitionFor(run);
      final workItemId = run.workItemId;
      return AgentRunDetailPanel(
        run: run,
        agentLabel: definition?.name,
        agentTypeLabel: definition == null
            ? null
            : EnumLabels.workItemType(
                definition.agentType,
                rawValue: definition.rawAgentType,
              ),
        workItemTitle: workItemId == null
            ? null
            : workItemTitleById[workItemId],
        onViewWorkItem: workItemId == null || onViewWorkItem == null
            ? null
            : () => onViewWorkItem!(workItemId),
      );
    });

    return ResponsiveBuilder(
      builder: (context, sizeClass) => sizeClass.isCompact
          ? Column(
              children: [
                list,
                const SizedBox(height: AppSpacing.md),
                detail,
              ],
            )
          : Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: list),
                const SizedBox(width: AppSpacing.md),
                Expanded(child: detail),
              ],
            ),
    );
  }
}

class _StatusFilterRow extends StatelessWidget {
  const _StatusFilterRow({required this.controller});

  final AgentRunsController controller;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        // Side by side when there is room; stacked, with the dropdown taking
        // the full width, on a narrow window or with enlarged text — the
        // longest German status does not fit beside its label at 200 %.
        final stacked =
            constraints.maxWidth <
            360 * MediaQuery.textScalerOf(context).scale(1);

        return Obx(() {
          final dropdown = DropdownButton<AgentRunStatus?>(
            value: controller.statusFilter.value,
            onChanged: controller.setStatusFilter,
            isExpanded: stacked,
            items: <DropdownMenuItem<AgentRunStatus?>>[
              DropdownMenuItem(child: Text('agentRuns.filter.all'.tr())),
              for (final status in AgentRunStatus.values)
                if (status != AgentRunStatus.unknown)
                  DropdownMenuItem(
                    value: status,
                    child: Text(EnumLabels.agentRunStatus(status)),
                  ),
            ],
          );
          final label = Text('agentRuns.filter.label'.tr());

          if (stacked) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [label, dropdown],
            );
          }
          return Row(
            children: [
              label,
              const SizedBox(width: AppSpacing.sm),
              dropdown,
            ],
          );
        });
      },
    );
  }
}

class _WorkItemFilterBanner extends StatelessWidget {
  const _WorkItemFilterBanner({
    required this.workItemId,
    required this.workItemTitleById,
    required this.onClear,
  });

  final String? workItemId;
  final Map<String, String> workItemTitleById;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context) {
    final id = workItemId;
    if (id == null) return const SizedBox.shrink();

    final theme = Theme.of(context);
    final title = workItemTitleById[id] ?? id;

    return Padding(
      padding: const EdgeInsets.only(bottom: AppSpacing.sm),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'agentRuns.workItemFilter.active'.tr(args: [title]),
              style: theme.textTheme.bodySmall,
            ),
          ),
          TextButton(
            onPressed: onClear,
            child: Text('agentRuns.workItemFilter.clear'.tr()),
          ),
        ],
      ),
    );
  }
}

class _AgentRunList extends StatelessWidget {
  const _AgentRunList({
    required this.data,
    required this.selectedId,
    required this.workItemFilter,
    required this.workItemTitleById,
    required this.onSelect,
  });

  final AgentRunsData data;
  final String? selectedId;
  final String? workItemFilter;
  final Map<String, String> workItemTitleById;
  final void Function(String) onSelect;

  @override
  Widget build(BuildContext context) {
    final runs = workItemFilter == null
        ? data.runs
        : data.runs.where((r) => r.workItemId == workItemFilter).toList();

    if (runs.isEmpty) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: AppSpacing.sm),
        child: Text(
          'agentRuns.workItemFilter.noMatches'.tr(),
          style: Theme.of(context).textTheme.bodyMedium
              ?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant),
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final run in runs) ...[
          AgentRunTile(
            run: run,
            agentLabel: data.definitionFor(run)?.name,
            workItemLabel: run.workItemId == null
                ? null
                : (workItemTitleById[run.workItemId] ?? run.workItemId),
            selected: run.id == selectedId,
            onTap: () => onSelect(run.id),
          ),
          const SizedBox(height: AppSpacing.xs),
        ],
      ],
    );
  }
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull => isEmpty ? null : first;
}
