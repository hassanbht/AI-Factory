import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';
import '../../domain/models/work_item.dart';
import '../models/dependency_graph_validation.dart';
import 'focus_ring.dart';
import 'status_chip.dart';

/// The plan's dependency edges, drawn as a tree — `docs/flutter-ux-spec.md`
/// §8: rows, not a graph canvas. Roots are the items nothing depends on;
/// each row's children are the items that depend on it.
///
/// Read-only: there is no drag, no edit, and no way to change an edge from
/// here — the backend owns dependencies, and the only way to submit them is
/// `POST /api/projects/{id}/plan`, which this screen does not call.
class WorkItemDependencyTree extends StatelessWidget {
  const WorkItemDependencyTree({
    required this.plan,
    required this.warnings,
    this.selectedPlanKey,
    this.onSelect,
    super.key,
  });

  final ProjectPlan plan;
  final List<DependencyWarning> warnings;
  final String? selectedPlanKey;
  final ValueChanged<WorkItemNode>? onSelect;

  @override
  Widget build(BuildContext context) {
    final rows = flattenDependencyTree(plan);

    return LayoutBuilder(
      builder: (context, constraints) {
        // Indentation shows depth, but never takes more than a third of the
        // width: a deep chain must not squeeze the titles to nothing, or
        // push a row past the edge. Structure is still announced: each
        // row's label names what it depends on.
        final maxIndent = constraints.maxWidth.isFinite
            ? constraints.maxWidth / 3
            : double.infinity;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (final row in rows)
              _DependencyRow(
                row: row,
                indent: (row.depth * AppSpacing.lg).clamp(0, maxIndent),
                selectedPlanKey: selectedPlanKey,
                onSelect: onSelect,
              ),
            if (warnings.isNotEmpty) ...[
              const SizedBox(height: AppSpacing.sm),
              Text(
                'dependency.warning.present'.plural(warnings.length),
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ],
        );
      },
    );
  }
}

class _DependencyRow extends StatelessWidget {
  const _DependencyRow({
    required this.row,
    required this.indent,
    required this.selectedPlanKey,
    required this.onSelect,
  });

  final DependencyTreeRow row;
  final double indent;

  final String? selectedPlanKey;
  final ValueChanged<WorkItemNode>? onSelect;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final node = row.node;
    final isShared = node.dependsOnPlanKeys.toSet().length > 1;
    final isCycle = row.isCycle;
    final selected = node.planKey == selectedPlanKey;

    final chip = StatusChip.workItem(
      node.workItem.status,
      rawStatus: node.workItem.rawStatus,
      compact: true,
    );

    final dependencyNames = node.dependsOnPlanKeys.join(', ');
    final identity = dependencyNames.isEmpty
        ? '${node.planKey} ${node.workItem.title}'
        : 'dependency.semantics.dependsOn'.tr(
            args: [node.planKey, node.workItem.title, dependencyNames],
          );
    // The status and a cycle are part of the announcement: the chip and the
    // warning icon are excluded from the tree below.
    final semanticsLabel = [
      identity,
      chip.label,
      if (isCycle) 'dependency.warning.cycle'.tr(),
    ].join(', ');

    final markers = <Widget>[
      chip,
      if (isShared)
        Tooltip(
          message: 'workItem.shared'.tr(),
          child: Icon(
            Icons.call_split,
            size: 14,
            color: scheme.onSurfaceVariant,
          ),
        ),
      if (isCycle)
        Tooltip(
          message: 'dependency.warning.cycle'.tr(),
          child: Icon(Icons.warning_amber, size: 14, color: scheme.error),
        ),
    ];

    final key = SizedBox(
      width: 36,
      child: Text(
        node.planKey,
        style: theme.textTheme.labelSmall?.copyWith(
          color: scheme.onSurfaceVariant,
        ),
        overflow: TextOverflow.ellipsis,
      ),
    );

    final rowWidget = Semantics(
      button: onSelect != null,
      selected: selected,
      label: semanticsLabel,
      excludeSemantics: true,
      child: FocusRing(
        borderRadius: BorderRadius.circular(AppRadius.sm),
        child: InkWell(
          onTap: onSelect == null || isCycle ? null : () => onSelect!(node),
          borderRadius: BorderRadius.circular(AppRadius.sm),
          child: Container(
            padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.sm,
              vertical: AppSpacing.xs,
            ),
            decoration: BoxDecoration(
              color: selected
                  ? scheme.primaryContainer.withValues(alpha: 0.35)
                  : null,
              borderRadius: BorderRadius.circular(AppRadius.sm),
            ),
            child: LayoutBuilder(
              builder: (context, constraints) {
                // One line when the title, the status and the markers fit;
                // otherwise the markers move under the title, which then keeps
                // two lines rather than being truncated to a few letters.
                final stacked =
                    constraints.maxWidth <
                    360 * MediaQuery.textScalerOf(context).scale(1);

                if (stacked) {
                  return Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      key,
                      const SizedBox(width: AppSpacing.xs),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              node.workItem.title,
                              style: theme.textTheme.bodySmall,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                            const SizedBox(height: AppSpacing.xs),
                            Wrap(
                              spacing: AppSpacing.xs,
                              runSpacing: AppSpacing.xs,
                              crossAxisAlignment: WrapCrossAlignment.center,
                              children: markers,
                            ),
                          ],
                        ),
                      ),
                    ],
                  );
                }

                return Row(
                  children: [
                    key,
                    const SizedBox(width: AppSpacing.xs),
                    Expanded(
                      child: Text(
                        node.workItem.title,
                        style: theme.textTheme.bodySmall,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    for (final marker in markers) ...[
                      const SizedBox(width: AppSpacing.xs),
                      marker,
                    ],
                  ],
                );
              },
            ),
          ),
        ),
      ),
    );

    return Padding(
      padding: EdgeInsets.only(left: indent),
      child: rowWidget,
    );
  }
}
