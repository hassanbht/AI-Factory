import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/localization/enum_labels.dart';
import '../../core/theme/app_spacing.dart';
import '../../core/theme/app_theme.dart';
import '../../domain/enums/backend_enums.dart';

/// How a backend state is rendered: colour plus icon.
///
/// Colour never carries meaning on its own — every chip also shows an icon
/// and a localized word, so the screen stays readable in greyscale and for a
/// colour-blind operator.
enum StatusTone {
  neutral(Icons.circle_outlined),
  active(Icons.autorenew),
  attention(Icons.how_to_reg_outlined),
  success(Icons.check_circle_outline),
  warning(Icons.pause_circle_outline),
  danger(Icons.error_outline);

  const StatusTone(this.icon);

  final IconData icon;

  Color color(ColorScheme scheme) => switch (this) {
    StatusTone.neutral => StatusPalette.neutral(scheme),
    StatusTone.active => StatusPalette.active(scheme),
    StatusTone.attention => StatusPalette.attention(scheme),
    StatusTone.success => StatusPalette.success(scheme),
    StatusTone.warning => StatusPalette.warning(scheme),
    StatusTone.danger => StatusPalette.danger(scheme),
  };
}

/// Maps backend enums onto a tone. One place, so a project in `HumanReview`
/// looks the same on the Dashboard, in the list and — from Phase 19.4 — on
/// Project Detail.
class StatusTones {
  const StatusTones._();

  static StatusTone project(ProjectStatus status) => switch (status) {
    ProjectStatus.created => StatusTone.neutral,
    ProjectStatus.humanReview => StatusTone.attention,
    ProjectStatus.paused => StatusTone.warning,
    ProjectStatus.completed => StatusTone.success,
    ProjectStatus.failed => StatusTone.danger,
    ProjectStatus.cancelled => StatusTone.neutral,
    ProjectStatus.repairing => StatusTone.warning,
    ProjectStatus.unknown => StatusTone.neutral,
    _ => StatusTone.active,
  };

  static StatusTone workItem(WorkItemStatus status) => switch (status) {
    WorkItemStatus.draft => StatusTone.neutral,
    WorkItemStatus.ready => StatusTone.neutral,
    WorkItemStatus.completed => StatusTone.success,
    WorkItemStatus.failed => StatusTone.danger,
    WorkItemStatus.blocked => StatusTone.warning,
    WorkItemStatus.repairing => StatusTone.warning,
    WorkItemStatus.cancelled => StatusTone.neutral,
    WorkItemStatus.unknown => StatusTone.neutral,
    _ => StatusTone.active,
  };

  static StatusTone agentRun(AgentRunStatus status) => switch (status) {
    AgentRunStatus.succeeded => StatusTone.success,
    AgentRunStatus.failed || AgentRunStatus.timedOut => StatusTone.danger,
    AgentRunStatus.cancelled => StatusTone.neutral,
    AgentRunStatus.created || AgentRunStatus.queued => StatusTone.neutral,
    AgentRunStatus.waiting => StatusTone.warning,
    AgentRunStatus.unknown => StatusTone.neutral,
    AgentRunStatus.running => StatusTone.active,
  };
}

/// A compact, localized state indicator.
///
/// Built for reuse: Project Detail, WorkItems, Agent Runs and Human Review
/// all render states, and all of them use this rather than inventing another
/// chip. It takes an already-localized [label] so it stays independent of
/// which enum it is showing.
class StatusChip extends StatelessWidget {
  const StatusChip({
    required this.label,
    required this.tone,
    this.semanticPrefix,
    this.compact = false,
    super.key,
  });

  /// Project status, the common case.
  factory StatusChip.project(
    ProjectStatus status, {
    String? rawStatus,
    bool compact = false,
    Key? key,
  }) => StatusChip(
    key: key,
    label: EnumLabels.projectStatus(status, rawValue: rawStatus),
    tone: StatusTones.project(status),
    semanticPrefix: 'projects.field.status'.tr(),
    compact: compact,
  );

  final String label;
  final StatusTone tone;

  /// Read out before the label, so a screen reader announces "Status:
  /// Human review" rather than a bare word.
  final String? semanticPrefix;

  final bool compact;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final color = tone.color(scheme);
    final textStyle = Theme.of(context).textTheme.labelMedium?.copyWith(
      color: color,
      fontWeight: FontWeight.w600,
    );

    return Semantics(
      label: semanticPrefix == null ? label : '$semanticPrefix: $label',
      excludeSemantics: true,
      child: Container(
        padding: EdgeInsets.symmetric(
          horizontal: compact ? AppSpacing.sm : AppSpacing.sm + 2,
          vertical: AppSpacing.xs,
        ),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(AppRadius.sm),
          border: Border.all(color: color.withValues(alpha: 0.45)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(tone.icon, size: 14, color: color),
            const SizedBox(width: AppSpacing.xs),
            // The label must never be clipped: German status words are long,
            // and a truncated state is a misleading state.
            Flexible(
              child: Text(label, style: textStyle, overflow: TextOverflow.fade),
            ),
          ],
        ),
      ),
    );
  }
}
