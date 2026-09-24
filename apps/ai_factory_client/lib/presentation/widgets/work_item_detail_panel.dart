import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/localization/enum_labels.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/work_item.dart';
import 'error_panel.dart';
import 'panels.dart';
import 'status_chip.dart';

/// The selected work item, in full: everything Workstream D of the Phase
/// 19.5 spec asks for and nothing `WorkItemDto` does not actually return.
///
/// [dependencyTitles] is the selected node's direct dependencies, already
/// resolved to display text by the caller — this panel never walks the plan
/// itself, so it stays reusable for the flat (no-plan) list too, where the
/// list is simply empty.
class WorkItemDetailPanel extends StatelessWidget {
  const WorkItemDetailPanel({
    required this.workItem,
    required this.readinessState,
    this.planKey,
    this.dependencyTitles = const <String>[],
    this.onViewAgentRuns,
    super.key,
  });

  final WorkItem workItem;
  final String? planKey;
  final List<String> dependencyTitles;
  final ViewState<TaskReadiness> readinessState;

  /// Navigates to this work item's agent runs. Null hides the action.
  final VoidCallback? onViewAgentRuns;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final description = workItem.description?.trim();

    return SectionCard(
      title: workItem.title,
      subtitle: planKey,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: description == null || description.isEmpty
                ? Text(
                    'workItem.noDescription'.tr(),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  )
                : SelectableText(
                    description,
                    style: theme.textTheme.bodyMedium,
                  ),
          ),
          const SizedBox(height: AppSpacing.md),
          FactList(
            facts: <Fact>[
              Fact(
                label: 'workItem.field.status'.tr(),
                child: StatusChip.workItem(
                  workItem.status,
                  rawStatus: workItem.rawStatus,
                ),
              ),
              Fact(
                label: 'workItem.field.readiness'.tr(),
                child: readinessState.map(
                  idle: () => Text('common.notAvailable'.tr()),
                  loading: () => const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                  refreshing: (r) => StatusChip.readiness(r),
                  success: (r) => StatusChip.readiness(r),
                  partial: (r, _) => StatusChip.readiness(r),
                  empty: () => Text('common.notAvailable'.tr()),
                  failure: (error, stale) => stale != null
                      ? StatusChip.readiness(stale)
                      : Text(
                          'common.notAvailable'.tr(),
                          style: theme.textTheme.bodySmall,
                        ),
                ),
              ),
              Fact(
                label: 'workItem.field.attempts'.tr(),
                child: Text(
                  '${workItem.attemptCount}',
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              Fact(
                label: 'workItem.field.repairs'.tr(),
                child: Text(
                  '${workItem.repairAttemptCount}',
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              Fact(
                label: 'workItem.field.type'.tr(),
                child: Text(
                  EnumLabels.workItemType(
                    workItem.type,
                    rawValue: workItem.rawType,
                  ),
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              Fact(
                label: 'workItem.field.priority'.tr(),
                child: Text(
                  EnumLabels.workItemPriority(
                    workItem.priority,
                    rawValue: workItem.rawPriority,
                  ),
                  style: theme.textTheme.bodyMedium,
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.md),
          Fact(
            label: 'workItem.field.dependencies'.tr(),
            child: dependencyTitles.isEmpty
                ? Text(
                    'workItem.noDependencies'.tr(),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  )
                : Wrap(
                    spacing: AppSpacing.sm,
                    runSpacing: AppSpacing.xs,
                    children: [
                      for (final title in dependencyTitles)
                        Chip(
                          label: Text(title),
                          visualDensity: VisualDensity.compact,
                        ),
                    ],
                  ),
          ),
          if (onViewAgentRuns != null) ...[
            const SizedBox(height: AppSpacing.md),
            Align(
              alignment: Alignment.centerLeft,
              child: OutlinedButton(
                onPressed: onViewAgentRuns,
                child: Text('workItem.action.viewAgentRuns'.tr()),
              ),
            ),
          ],
          const SizedBox(height: AppSpacing.md),
          UnavailableNote(
            title: 'workItem.acceptance.title'.tr(),
            reason: 'workItem.acceptance.reason'.tr(),
          ),
          if (workItem.hasError) ...[
            const SizedBox(height: AppSpacing.md),
            RecordedErrorPanel(
              title: 'workItem.field.currentError'.tr(),
              message: workItem.currentError!,
              footnote: 'workItem.error.noCategory'.tr(),
            ),
          ],
        ],
      ),
    );
  }
}
