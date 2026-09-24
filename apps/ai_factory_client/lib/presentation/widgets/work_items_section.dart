import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/work_item.dart';
import '../controllers/work_items_controller.dart';
import '../models/work_items_data.dart';
import 'panels.dart';
import 'state_views.dart';
import 'work_item_detail_panel.dart';
import 'work_item_dependency_tree.dart';
import 'work_item_tile.dart';

/// Work Items and Dependencies, together — the section Phase 19.5 adds to
/// Project Detail. Selecting a row in the list drives the detail panel and
/// highlights the same node in the tree, so the two views never disagree
/// about which work item is open.
class WorkItemsSection extends StatelessWidget {
  const WorkItemsSection({
    required this.controller,
    this.onViewAgentRuns,
    super.key,
  });

  final WorkItemsController controller;

  /// Navigates to the selected work item's agent runs. Null hides the action.
  final ValueChanged<String>? onViewAgentRuns;

  @override
  Widget build(BuildContext context) {
    return Obx(() {
      final state = controller.state.value;
      return AsyncView<WorkItemsData>(
        state: state,
        onRetry: controller.load,
        emptyTitle: 'workItems.empty.title'.tr(),
        emptyMessage: 'workItems.empty.message'.tr(),
        builder: (context, data) => _WorkItemsBody(
          controller: controller,
          data: data,
          onViewAgentRuns: onViewAgentRuns,
        ),
      );
    });
  }
}

class _WorkItemsBody extends StatelessWidget {
  const _WorkItemsBody({
    required this.controller,
    required this.data,
    required this.onViewAgentRuns,
  });

  final WorkItemsController controller;
  final WorkItemsData data;
  final ValueChanged<String>? onViewAgentRuns;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final completedCount = data.workItems
        .where((item) => item.status == WorkItemStatus.completed)
        .length;

    final list = SectionCard(
      title: 'workItems.title'.tr(),
      subtitle: 'workItems.summary'.plural(
        data.workItems.length,
        args: ['${data.workItems.length}', '$completedCount'],
      ),
      child: Obx(
        () => _WorkItemList(
          data: data,
          selectedId: controller.selectedWorkItemId.value,
          onSelect: controller.selectWorkItem,
        ),
      ),
    );

    final detail = Obx(() {
      final selectedId = controller.selectedWorkItemId.value;
      final workItem = selectedId == null
          ? null
          : data.workItems.where((w) => w.id == selectedId).firstOrNull;
      if (workItem == null) {
        return SectionCard(
          title: 'workItem.detail.title'.tr(),
          child: Text(
            'workItem.detail.none'.tr(),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        );
      }

      final node = data.plan?.nodes
          .where((n) => n.workItem.id == selectedId)
          .firstOrNull;
      final dependencyTitles = <String>[
        if (node != null)
          for (final depId in node.dependsOnWorkItemIds)
            data.workItems.where((w) => w.id == depId).firstOrNull?.title ??
                depId,
      ];

      return WorkItemDetailPanel(
        workItem: workItem,
        planKey: node?.planKey,
        dependencyTitles: dependencyTitles,
        readinessState: controller.readinessState.value,
        onViewAgentRuns: onViewAgentRuns == null
            ? null
            : () => onViewAgentRuns!(workItem.id),
      );
    });

    final plan = data.plan;
    final dependencies = SectionCard(
      title: 'dependency.title'.tr(),
      child: plan == null || plan.nodes.isEmpty
          ? UnavailableNote(
              title: 'dependency.noPlan.title'.tr(),
              reason: 'dependency.noPlan.reason'.tr(),
            )
          : Obx(
              () => WorkItemDependencyTree(
                plan: plan,
                warnings: data.warnings,
                selectedPlanKey: plan.nodes
                    .where(
                      (n) =>
                          n.workItem.id == controller.selectedWorkItemId.value,
                    )
                    .firstOrNull
                    ?.planKey,
                onSelect: (node) => controller.selectWorkItem(node.workItem.id),
              ),
            ),
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ResponsiveBuilder(
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
        ),
        const SizedBox(height: AppSpacing.md),
        dependencies,
      ],
    );
  }
}

class _WorkItemList extends StatelessWidget {
  const _WorkItemList({
    required this.data,
    required this.selectedId,
    required this.onSelect,
  });

  final WorkItemsData data;
  final String? selectedId;
  final void Function(String) onSelect;

  @override
  Widget build(BuildContext context) {
    final plan = data.plan;
    final nodeByWorkItemId = <String, WorkItemNode>{
      if (plan != null)
        for (final node in plan.nodes) node.workItem.id: node,
    };

    if (!data.hasPlan) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          UnavailableNote(
            title: 'workItems.noPlan.title'.tr(),
            reason: 'workItems.noPlan.reason'.tr(),
          ),
          const SizedBox(height: AppSpacing.sm),
          for (final item in data.workItems) ...[
            WorkItemTile(
              workItem: item,
              selected: item.id == selectedId,
              onTap: () => onSelect(item.id),
            ),
            const SizedBox(height: AppSpacing.xs),
          ],
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final item in data.workItems) ...[
          WorkItemTile(
            workItem: item,
            planKey: nodeByWorkItemId[item.id]?.planKey,
            readiness: nodeByWorkItemId[item.id]?.readiness,
            isShared:
                (nodeByWorkItemId[item.id]?.dependsOnPlanKeys.toSet().length ??
                    0) >
                1,
            selected: item.id == selectedId,
            onTap: () => onSelect(item.id),
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
