import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/localization/relative_time.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import 'status_chip.dart';

/// One project, in the list.
///
/// A single widget with a single data binding, arranged two ways: columns
/// from tablet width upwards, a stacked card below it. There is no second
/// implementation and no per-breakpoint logic — only the arrangement of the
/// same four pieces (identity, status, last update, actions) changes.
class ProjectListTile extends StatelessWidget {
  const ProjectListTile({
    required this.project,
    required this.onOpen,
    this.onStart,
    this.isStarting = false,
    super.key,
  });

  final Project project;
  final VoidCallback onOpen;

  /// Null when starting makes no sense for this project — see [canStart].
  final VoidCallback? onStart;

  /// A Start request for **this** project is in flight.
  final bool isStarting;

  /// Whether the Start action is offered at all.
  ///
  /// This is a display rule, not a lifecycle decision: the only thing checked
  /// is the `lifecycleStartedAtUtc` the API returned, which says the project
  /// has already been started. Whether an unstarted project may actually
  /// start — source initialized, description present — is the backend's call,
  /// and it answers with a `409` the screen shows.
  static bool canStart(Project project) => !project.isStarted;

  @override
  Widget build(BuildContext context) {
    return ResponsiveBuilder(
      builder: (context, sizeClass) {
        final content = sizeClass.isCompact
            ? _StackedLayout(
                project: project,
                onOpen: onOpen,
                onStart: onStart,
                isStarting: isStarting,
              )
            : _ColumnLayout(
                project: project,
                onOpen: onOpen,
                onStart: onStart,
                isStarting: isStarting,
              );

        return Semantics(
          button: true,
          label: _semanticLabel(context),
          child: Card(
            child: InkWell(
              onTap: onOpen,
              borderRadius: BorderRadius.circular(AppRadius.md),
              child: Padding(
                padding: const EdgeInsets.all(AppSpacing.md),
                child: content,
              ),
            ),
          ),
        );
      },
    );
  }

  String _semanticLabel(BuildContext context) {
    final status = 'projects.field.status'.tr();
    final label = project.status == ProjectStatus.unknown
        ? project.rawStatus
        : 'enums.projectStatus.${project.status.name}'.tr();
    return '${project.name}, $status: $label';
  }
}

/// Name and description — the part that identifies the project. Shared by
/// both arrangements so the truncation rules cannot drift apart.
class _Identity extends StatelessWidget {
  const _Identity({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          project.name,
          style: theme.textTheme.titleSmall,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        const SizedBox(height: 2),
        Text(
          // The slug is the backend's own identifier for the project and is
          // never translated.
          project.slug,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        if (project.status == ProjectStatus.created) ...[
          const SizedBox(height: AppSpacing.xs),
          Text(
            'projects.sourceNotInitialized'.tr(),
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
              fontStyle: FontStyle.italic,
            ),
          ),
        ],
      ],
    );
  }
}

class _UpdatedAt extends StatelessWidget {
  const _UpdatedAt({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = context.locale.toString();

    return Tooltip(
      message: RelativeTime.absolute(project.updatedAtUtc, locale),
      child: Text(
        RelativeTime.format(project.updatedAtUtc, locale),
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _Actions extends StatelessWidget {
  const _Actions({
    required this.onOpen,
    required this.onStart,
    required this.isStarting,
  });

  final VoidCallback onOpen;
  final VoidCallback? onStart;
  final bool isStarting;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (onStart != null) ...[
          // Disabled while the request is in flight: one press, one request.
          OutlinedButton.icon(
            onPressed: isStarting ? null : onStart,
            icon: isStarting
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.play_arrow, size: 18),
            label: Text('projects.action.start'.tr()),
          ),
          const SizedBox(width: AppSpacing.sm),
        ],
        FilledButton.tonal(
          onPressed: onOpen,
          child: Text('projects.action.open'.tr()),
        ),
      ],
    );
  }
}

class _ColumnLayout extends StatelessWidget {
  const _ColumnLayout({
    required this.project,
    required this.onOpen,
    required this.onStart,
    required this.isStarting,
  });

  final Project project;
  final VoidCallback onOpen;
  final VoidCallback? onStart;
  final bool isStarting;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(flex: 4, child: _Identity(project: project)),
        const SizedBox(width: AppSpacing.md),
        Expanded(
          flex: 2,
          child: Align(
            alignment: Alignment.centerLeft,
            child: StatusChip.project(
              project.status,
              rawStatus: project.rawStatus,
            ),
          ),
        ),
        const SizedBox(width: AppSpacing.md),
        Expanded(flex: 2, child: _UpdatedAt(project: project)),
        const SizedBox(width: AppSpacing.md),
        _Actions(onOpen: onOpen, onStart: onStart, isStarting: isStarting),
      ],
    );
  }
}

class _StackedLayout extends StatelessWidget {
  const _StackedLayout({
    required this.project,
    required this.onOpen,
    required this.onStart,
    required this.isStarting,
  });

  final Project project;
  final VoidCallback onOpen;
  final VoidCallback? onStart;
  final bool isStarting;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _Identity(project: project),
        const SizedBox(height: AppSpacing.sm),
        // Wrap rather than Row: a long German status label next to a long
        // relative time would overflow a narrow phone width.
        Wrap(
          spacing: AppSpacing.sm,
          runSpacing: AppSpacing.xs,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            StatusChip.project(
              project.status,
              rawStatus: project.rawStatus,
              compact: true,
            ),
            _UpdatedAt(project: project),
          ],
        ),
        const SizedBox(height: AppSpacing.sm),
        Align(
          alignment: Alignment.centerRight,
          child: _Actions(
            onOpen: onOpen,
            onStart: onStart,
            isStarting: isStarting,
          ),
        ),
      ],
    );
  }
}

/// The column captions above the list, shown only where the rows are laid out
/// in columns.
class ProjectListHeader extends StatelessWidget {
  const ProjectListHeader({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final style = theme.textTheme.labelMedium?.copyWith(
      color: theme.colorScheme.onSurfaceVariant,
    );

    return ResponsiveBuilder(
      builder: (context, sizeClass) {
        if (sizeClass.isCompact) return const SizedBox.shrink();

        return Padding(
          padding: const EdgeInsets.symmetric(
            horizontal: AppSpacing.md,
            vertical: AppSpacing.sm,
          ),
          child: Row(
            children: [
              Expanded(
                flex: 4,
                child: Text('projects.field.name'.tr(), style: style),
              ),
              const SizedBox(width: AppSpacing.md),
              Expanded(
                flex: 2,
                child: Text('projects.field.status'.tr(), style: style),
              ),
              const SizedBox(width: AppSpacing.md),
              Expanded(
                flex: 2,
                child: Text('projects.field.updated'.tr(), style: style),
              ),
              const SizedBox(width: AppSpacing.md),
              Text('projects.field.actions'.tr(), style: style),
            ],
          ),
        );
      },
    );
  }
}
