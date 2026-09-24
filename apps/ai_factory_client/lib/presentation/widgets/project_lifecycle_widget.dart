import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/localization/enum_labels.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../models/project_lifecycle.dart';
import 'panels.dart';
import 'status_chip.dart';

/// The project lifecycle, drawn from the backend's own transition table.
///
/// What it shows and what it refuses to show are equally deliberate:
///
/// * The **track** is the forward chain in
///   `AI.Factory.Domain.Entities.ProjectTransitions`. No edge is invented.
/// * The **current state** is whatever `GET /api/projects/{id}` returned, and
///   it is the visually dominant element.
/// * No stage is marked "completed" and no stage carries a timestamp, because
///   the API exposes one status and no history (gap `G-STAGE-TIMESTAMPS`).
///   Stages are placed *relative to* the current one, and the card says so in
///   words at the bottom.
/// * `Paused`, `Failed` and `Cancelled` overwrite `status` on the backend, so
///   the stage they interrupted is unknown. The widget then marks **no** stage
///   as current and states why, rather than guessing one.
///
/// Reusable on purpose: Human Review (Phase 19.7) shows the same track.
class ProjectLifecycleWidget extends StatelessWidget {
  const ProjectLifecycleWidget({required this.lifecycle, super.key});

  final ProjectLifecycle lifecycle;

  @override
  Widget build(BuildContext context) {
    // This widget is built for reuse — Human Review shows the same track — and
    // its own strings touch no other inherited value, so it registers the
    // locale dependency itself rather than relying on whoever embeds it.
    final _ = context.locale;

    return SectionCard(
      title: 'lifecycle.title'.tr(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CurrentState(lifecycle: lifecycle),
          const SizedBox(height: AppSpacing.lg),
          _Track(lifecycle: lifecycle),
          const SizedBox(height: AppSpacing.lg),
          UnavailableNote(
            title: 'lifecycle.history.title'.tr(),
            reason: 'lifecycle.history.reason'.tr(),
          ),
        ],
      ),
    );
  }
}

/// The state the server reported, made dominant.
///
/// Everything here is localized display text over an untouched wire value:
/// `HumanReview` reads "Human review" / "Menschliche Freigabe" and is still
/// `HumanReview` on the way back to the API.
class _CurrentState extends StatelessWidget {
  const _CurrentState({required this.lifecycle});

  final ProjectLifecycle lifecycle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final tone = StatusTones.project(lifecycle.status);
    final color = tone.color(theme.colorScheme);
    final label = EnumLabels.projectStatus(
      lifecycle.status,
      rawValue: lifecycle.rawStatus,
    );
    final note = _noteKey();

    return Semantics(
      container: true,
      label: '${'lifecycle.currentState'.tr()}: $label',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'lifecycle.currentState'.tr(),
            style: theme.textTheme.labelMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
          Row(
            children: [
              Icon(tone.icon, color: color, size: 28),
              const SizedBox(width: AppSpacing.sm),
              // The status word is this screen's headline, so it wraps rather
              // than truncates: a clipped state is a misread state, and the
              // German labels are the long ones.
              Expanded(
                child: Text(
                  label,
                  style: theme.textTheme.headlineSmall?.copyWith(
                    color: color,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
            ],
          ),
          if (note != null) ...[
            const SizedBox(height: AppSpacing.sm),
            Text(
              note.tr(),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
    );
  }

  /// One sentence explaining what the reported state means for the track.
  ///
  /// Every off-track state gets one, because every off-track state is a case
  /// where the track on its own would mislead.
  String? _noteKey() {
    final offTrack = lifecycle.offTrack;
    if (offTrack != null) {
      return switch (offTrack) {
        OffTrackState.repairing => 'lifecycle.offTrack.repairing',
        OffTrackState.paused => 'lifecycle.offTrack.paused',
        OffTrackState.failed => 'lifecycle.offTrack.failed',
        OffTrackState.cancelled => 'lifecycle.offTrack.cancelled',
        OffTrackState.unrecognized => 'lifecycle.offTrack.unrecognized',
      };
    }

    return switch (lifecycle.status) {
      ProjectStatus.created => 'lifecycle.note.created',
      ProjectStatus.humanReview => 'lifecycle.note.humanReview',
      ProjectStatus.completed => 'lifecycle.note.completed',
      _ => lifecycle.isStarted ? null : 'lifecycle.note.notStarted',
    };
  }
}

/// The forward chain, wrapped rather than scrolled.
///
/// A `Wrap` is what keeps a ten-stage track and long German labels off a
/// horizontal scrollbar at every window size.
class _Track extends StatelessWidget {
  const _Track({required this.lifecycle});

  final ProjectLifecycle lifecycle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final children = <Widget>[];

    for (final node in lifecycle.nodes) {
      if (children.isNotEmpty) {
        children.add(
          ExcludeSemantics(
            child: Icon(
              Icons.chevron_right,
              size: 18,
              color: theme.colorScheme.outlineVariant,
            ),
          ),
        );
      }
      children.add(_StageNode(node: node, totalStages: lifecycle.nodes.length));
    }

    return Wrap(
      spacing: AppSpacing.xs,
      runSpacing: AppSpacing.sm,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: children,
    );
  }
}

class _StageNode extends StatelessWidget {
  const _StageNode({required this.node, required this.totalStages});

  final LifecycleNode node;
  final int totalStages;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    final (
      IconData icon,
      Color color,
      String positionKey,
    ) = switch (node.position) {
      LifecyclePosition.current => (
        StatusTones.project(node.status).icon,
        StatusTones.project(node.status).color(scheme),
        'lifecycle.position.current',
      ),
      LifecyclePosition.anchor => (
        Icons.loop,
        StatusTone.warning.color(scheme),
        'lifecycle.position.anchor',
      ),
      LifecyclePosition.earlier => (
        Icons.circle,
        scheme.onSurfaceVariant,
        'lifecycle.position.earlier',
      ),
      LifecyclePosition.later => (
        Icons.circle_outlined,
        scheme.outline,
        'lifecycle.position.later',
      ),
      LifecyclePosition.undetermined => (
        Icons.help_outline,
        scheme.outline,
        'lifecycle.position.undetermined',
      ),
    };

    final emphasized =
        node.position == LifecyclePosition.current ||
        node.position == LifecyclePosition.anchor;
    final label = EnumLabels.projectStatus(node.status);
    final position = positionKey.tr();
    final step = 'lifecycle.step'.tr(
      args: <String>['${node.index + 1}', '$totalStages'],
    );

    return Semantics(
      // "Step 6 of 10: Implementation. Current stage." — the position is part
      // of the announcement, so the track is readable without seeing colour.
      container: true,
      label: '$step: $label. $position',
      excludeSemantics: true,
      child: Container(
        padding: const EdgeInsets.symmetric(
          horizontal: AppSpacing.sm + 2,
          vertical: AppSpacing.sm,
        ),
        decoration: BoxDecoration(
          color: emphasized ? color.withValues(alpha: 0.12) : null,
          borderRadius: BorderRadius.circular(AppRadius.sm),
          border: Border.all(
            color: emphasized
                ? color.withValues(alpha: 0.45)
                : scheme.outlineVariant,
          ),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(icon, size: 14, color: color),
                const SizedBox(width: AppSpacing.xs),
                // Flexible, not fixed: "Menschliche Freigabe" is wider than a
                // node on a 360 dp window and must wrap rather than be cut —
                // a truncated stage name is a misread lifecycle.
                Flexible(
                  child: Text(
                    label,
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: emphasized ? color : scheme.onSurfaceVariant,
                      fontWeight: emphasized
                          ? FontWeight.w700
                          : FontWeight.w500,
                    ),
                  ),
                ),
              ],
            ),
            // Only the stages whose position is load-bearing say so on screen;
            // the rest carry it in their semantics label, so ten captions do
            // not crowd out the track itself.
            if (emphasized) ...[
              const SizedBox(height: AppSpacing.xs),
              Text(
                position,
                style: theme.textTheme.labelSmall?.copyWith(color: color),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
