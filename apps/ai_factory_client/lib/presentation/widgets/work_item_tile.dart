import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/work_item.dart';
import 'focus_ring.dart';
import 'status_chip.dart';

/// One row in the WorkItems list — identifier, title, status, readiness and
/// the attempt/repair counters, all of them fields the API actually returns
/// (`docs/flutter-api-contract.md` §4). No acceptance result is shown here:
/// `G-ACCEPTANCE` remains unresolved, and "Completed" is a WorkItem status,
/// not a claim that acceptance criteria passed.
class WorkItemTile extends StatelessWidget {
  const WorkItemTile({
    required this.workItem,
    this.planKey,
    this.readiness,
    this.isShared = false,
    this.selected = false,
    this.onTap,
    super.key,
  });

  final WorkItem workItem;

  /// `T01`-style identifier, when the item came from a plan. Null in the flat
  /// fallback list (no plan exists yet).
  final String? planKey;

  final TaskReadiness? readiness;

  /// This work item has more than one dependent parent in the tree, so it
  /// appears under several branches — marked rather than silently repeated.
  final bool isShared;

  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final identifier = planKey ?? workItem.id;

    final attempts = 'workItem.attempts'.plural(workItem.attemptCount);
    final repairs = workItem.hasBeenRepaired
        ? 'workItem.repairs'.plural(workItem.repairAttemptCount)
        : null;

    // Read as "Work item T03, Implement the endpoint, Status: Running,
    // Readiness: Running, 1 attempt" — every fact the row shows visually.
    final status = StatusChip.workItem(
      workItem.status,
      rawStatus: workItem.rawStatus,
    );
    final readinessChip = readiness == null
        ? null
        : StatusChip.readiness(readiness!);
    final semanticsLabel = <String>[
      '${'workItem.semanticsRole'.tr()} $identifier',
      workItem.title,
      '${status.semanticPrefix}: ${status.label}',
      if (readinessChip != null)
        '${readinessChip.semanticPrefix}: ${readinessChip.label}',
      attempts,
      ?repairs,
      if (isShared) 'workItem.shared'.tr(),
    ].join(', ');

    return Semantics(
      button: onTap != null,
      selected: selected,
      label: semanticsLabel,
      excludeSemantics: true,
      child: FocusRing(
        borderRadius: BorderRadius.circular(AppRadius.sm),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(AppRadius.sm),
          child: Container(
            padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.sm,
              vertical: AppSpacing.sm,
            ),
            decoration: BoxDecoration(
              color: selected
                  ? scheme.primaryContainer.withValues(alpha: 0.35)
                  : null,
              borderRadius: BorderRadius.circular(AppRadius.sm),
              border: Border.all(
                color: selected ? scheme.primary : scheme.outlineVariant,
              ),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 40,
                  child: Text(
                    identifier,
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Expanded(
                            child: Text(
                              workItem.title,
                              style: theme.textTheme.bodyMedium?.copyWith(
                                fontWeight: FontWeight.w600,
                              ),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          if (isShared) ...[
                            const SizedBox(width: AppSpacing.xs),
                            Icon(
                              Icons.call_split,
                              size: 14,
                              color: scheme.onSurfaceVariant,
                            ),
                          ],
                        ],
                      ),
                      const SizedBox(height: AppSpacing.xs),
                      Wrap(
                        spacing: AppSpacing.sm,
                        runSpacing: AppSpacing.xs,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [
                          StatusChip.workItem(
                            workItem.status,
                            rawStatus: workItem.rawStatus,
                            compact: true,
                          ),
                          if (readiness != null)
                            StatusChip.readiness(readiness!, compact: true),
                          Text(
                            repairs == null ? attempts : '$attempts · $repairs',
                            style: theme.textTheme.labelSmall?.copyWith(
                              color: scheme.onSurfaceVariant,
                            ),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
