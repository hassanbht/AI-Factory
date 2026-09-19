import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/errors/app_error.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';

/// Shared surfaces for the operator screens.
///
/// They exist so every screen states the same things the same way: what a
/// section is, what a number means, what is missing and why, and what went
/// wrong with an action the operator just took.

/// A titled block of content with an optional trailing action.
class SectionCard extends StatelessWidget {
  const SectionCard({
    required this.title,
    required this.child,
    this.subtitle,
    this.trailing,
    this.dense = false,
    super.key,
  });

  final String title;
  final String? subtitle;
  final Widget child;
  final Widget? trailing;
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: EdgeInsets.all(dense ? AppSpacing.md : AppSpacing.lg),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        title,
                        style: theme.textTheme.titleMedium,
                        // A heading, so a screen reader can jump between
                        // sections instead of reading the whole page.
                        semanticsLabel: title,
                      ),
                      if (subtitle != null) ...[
                        const SizedBox(height: AppSpacing.xs),
                        Text(
                          subtitle!,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
                ?trailing,
              ],
            ),
            const SizedBox(height: AppSpacing.md),
            child,
          ],
        ),
      ),
    );
  }
}

/// A single counted figure.
///
/// [onTap] is what turns a count into a way to see the projects behind it —
/// a number the operator cannot drill into is trivia.
class MetricTile extends StatelessWidget {
  const MetricTile({
    required this.label,
    required this.value,
    this.icon,
    this.color,
    this.onTap,
    super.key,
  });

  final String label;
  final String value;
  final IconData? icon;
  final Color? color;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent = color ?? theme.colorScheme.onSurface;

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(AppRadius.md),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                children: [
                  if (icon != null) ...[
                    Icon(icon, size: 16, color: accent),
                    const SizedBox(width: AppSpacing.xs),
                  ],
                  Expanded(
                    child: Text(
                      label,
                      style: theme.textTheme.labelMedium?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: AppSpacing.sm),
              Text(
                value,
                style: theme.textTheme.headlineSmall?.copyWith(
                  color: accent,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// States, in the operator's words, that something is **not** shown and why.
///
/// The alternative — omitting the tile, or filling it with a plausible
/// number — is how a console starts lying. Every gap in
/// `docs/flutter-api-gaps.md` that reaches the UI surfaces through this.
class UnavailableNote extends StatelessWidget {
  const UnavailableNote({required this.reason, this.title, super.key});

  final String reason;
  final String? title;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(
          Icons.info_outline,
          size: 16,
          color: theme.colorScheme.onSurfaceVariant,
        ),
        const SizedBox(width: AppSpacing.sm),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                title ?? 'common.notAvailable'.tr(),
                style: theme.textTheme.labelMedium,
              ),
              const SizedBox(height: AppSpacing.xs),
              Text(
                reason,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// A dismissible banner for a failed **action** (start, create), as opposed
/// to a failed screen load.
///
/// It sits above the content and leaves it visible: a rejected Start must not
/// blank out the list the operator is working in. The technical detail is
/// kept out of the message and shown only in the expandable block, so the
/// user-facing line is always a translated sentence.
class ActionErrorBanner extends StatelessWidget {
  const ActionErrorBanner({
    required this.error,
    this.onDismiss,
    this.onRetry,
    super.key,
  });

  final AppError error;
  final VoidCallback? onDismiss;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final detail = error.technicalDetail;

    return Semantics(
      liveRegion: true,
      container: true,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(AppSpacing.md),
        decoration: BoxDecoration(
          color: theme.colorScheme.errorContainer,
          borderRadius: BorderRadius.circular(AppRadius.md),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(
                  Icons.error_outline,
                  size: 20,
                  color: theme.colorScheme.onErrorContainer,
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    error.messageKey.tr(),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onErrorContainer,
                    ),
                  ),
                ),
                if (onRetry != null)
                  TextButton(
                    onPressed: onRetry,
                    child: Text('common.retry'.tr()),
                  ),
                if (onDismiss != null)
                  IconButton(
                    onPressed: onDismiss,
                    tooltip: 'common.close'.tr(),
                    icon: const Icon(Icons.close, size: 18),
                  ),
              ],
            ),
            if (detail != null) ...[
              const SizedBox(height: AppSpacing.xs),
              Text(
                detail,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onErrorContainer,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Lays tiles out in one, two or four columns depending on the window size
/// class — the same children in every case, arranged differently.
class ResponsiveTileGrid extends StatelessWidget {
  const ResponsiveTileGrid({required this.children, super.key});

  final List<Widget> children;

  static int columnsFor(WindowSizeClass sizeClass) => switch (sizeClass) {
    WindowSizeClass.compact => 1,
    WindowSizeClass.medium => 2,
    WindowSizeClass.expanded => 4,
  };

  @override
  Widget build(BuildContext context) {
    return ResponsiveBuilder(
      builder: (context, sizeClass) {
        final columns = columnsFor(sizeClass);
        return LayoutBuilder(
          builder: (context, constraints) {
            const spacing = AppSpacing.md;
            final width =
                (constraints.maxWidth - spacing * (columns - 1)) / columns;

            return Wrap(
              spacing: spacing,
              runSpacing: spacing,
              children: [
                for (final child in children)
                  SizedBox(width: width.clamp(1, constraints.maxWidth), child: child),
              ],
            );
          },
        );
      },
    );
  }
}
