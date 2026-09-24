import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';
import '../../domain/models/agent_run.dart';
import 'focus_ring.dart';
import 'status_chip.dart';
import 'timestamp_text.dart';

/// One row in the AgentRuns list — agent type, status, attempt and the
/// timestamp the backend actually reports for the run's phase, all fields
/// `AgentRunDto` returns (`docs/flutter-api-contract.md` §6). No model,
/// provider or fallback text here: `G-RUN-TELEMETRY`/`G-FALLBACK` remain
/// unresolved, and this row shows what it knows rather than a placeholder.
class AgentRunTile extends StatelessWidget {
  const AgentRunTile({
    required this.run,
    this.agentLabel,
    this.workItemLabel,
    this.selected = false,
    this.onTap,
    super.key,
  });

  final AgentRun run;

  /// The agent's display name (or its localized type as a fallback), already
  /// resolved by the caller from `AgentRunsData.definitionFor`.
  final String? agentLabel;

  /// The related work item's title (or its raw id as a fallback), read out
  /// as part of the row's accessibility label (Workstream X: "relevant
  /// WorkItem/project identification"). Not shown visually — the row is
  /// already project-scoped, so the title would repeat context every row
  /// already shares; a screen reader still needs it per item.
  final String? workItemLabel;

  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final label = agentLabel ?? 'agentRun.unknownAgent'.tr(args: [run.id]);
    final attempt = 'agentRun.attempt'.tr(args: ['${run.attempt}']);
    final timestamp = run.startedAtUtc ?? run.createdAtUtc;
    final timestampLabel = run.startedAtUtc != null
        ? 'agentRun.field.started'.tr()
        : 'agentRun.field.created'.tr();

    final chip = StatusChip.agentRun(run.status, rawStatus: run.rawStatus);
    // "Agent run: Implementation Agent, Status: Running, Attempt 1, Work
    // item: Implement the endpoint" — the role first, so the row is
    // understandable without the section heading around it.
    final semanticsLabel = <String>[
      '${'agentRun.semanticsRole'.tr()}: $label',
      '${chip.semanticPrefix}: ${chip.label}',
      attempt,
      if (workItemLabel != null)
        '${'agentRun.field.workItem'.tr()}: $workItemLabel',
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
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: AppSpacing.xs),
                Wrap(
                  spacing: AppSpacing.sm,
                  runSpacing: AppSpacing.xs,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    chip,
                    Text(
                      attempt,
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: scheme.onSurfaceVariant,
                      ),
                    ),
                    Text(
                      '$timestampLabel ',
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: scheme.onSurfaceVariant,
                      ),
                    ),
                    TimestampText(
                      timestamp: timestamp,
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: scheme.onSurfaceVariant,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
