import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';
import '../../domain/models/project.dart';
import 'panels.dart';
import 'status_chip.dart';
import 'timestamp_text.dart';

/// Project identity and the actions that belong to the project as a whole.
///
/// Every field comes from `GET /api/projects/{id}` and nothing else. Name,
/// slug and identifier are operator data and are rendered verbatim — never
/// translated, never shortened, never title-cased. Only the *status word* is
/// localized, over a wire value the client leaves untouched.
///
/// The panel owns no lifecycle authority: [onStart] is null whenever the
/// screen has decided not to offer the action, and whether the backend accepts
/// it remains the backend's answer.
class ProjectHeaderPanel extends StatelessWidget {
  const ProjectHeaderPanel({
    required this.project,
    this.onStart,
    this.isStarting = false,
    this.startUnavailableReason,
    super.key,
  });

  final Project project;

  /// Null when Start is not offered for this project.
  final VoidCallback? onStart;

  /// A Start request is in flight.
  final bool isStarting;

  /// Shown in place of the action when it is not offered and the operator
  /// would otherwise be left wondering why. Null when its absence is
  /// self-explanatory — a started project does not need to be told it started.
  final String? startUnavailableReason;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return SectionCard(
      title: project.name,
      trailing: onStart == null
          ? null
          : FilledButton.icon(
              onPressed: isStarting ? null : onStart,
              icon: isStarting
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.play_arrow),
              label: Text('projects.action.start'.tr()),
            ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          FactList(
            facts: <Fact>[
              Fact(
                label: 'projects.field.status'.tr(),
                child: StatusChip.project(
                  project.status,
                  rawStatus: project.rawStatus,
                ),
              ),
              Fact(
                label: 'project.field.identifier'.tr(),
                // The id is the join key into the audit log and into a server
                // log line, so it is selectable and never abbreviated.
                child: SelectableText(
                  project.id,
                  style: theme.textTheme.bodySmall,
                ),
              ),
              Fact(
                label: 'projectDetail.field.slug'.tr(),
                child: SelectableText(
                  project.slug,
                  style: theme.textTheme.bodySmall,
                ),
              ),
              Fact(
                label: 'projects.field.created'.tr(),
                child: TimestampText(timestamp: project.createdAtUtc),
              ),
              Fact(
                label: 'projects.field.updated'.tr(),
                child: TimestampText(timestamp: project.updatedAtUtc),
              ),
              Fact(
                label: 'project.field.started'.tr(),
                child: TimestampText.orText(
                  timestamp: project.lifecycleStartedAtUtc,
                  fallback: 'project.notStarted'.tr(),
                ),
              ),
            ],
          ),
          if (onStart == null && startUnavailableReason != null) ...[
            const SizedBox(height: AppSpacing.md),
            UnavailableNote(
              title: 'projects.action.start'.tr(),
              reason: startUnavailableReason!,
            ),
          ],
        ],
      ),
    );
  }
}
