import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';

/// An error the **backend recorded on a resource**, as opposed to an error
/// this client's request produced.
///
/// The two are different things and must not look alike: `ActionErrorBanner`
/// reports "your request failed", while this reports "the server is holding a
/// failure on this object". This one is therefore not dismissible — it goes
/// away when the server stops reporting it, not when the operator clicks.
///
/// [message] is server text, shown verbatim and never translated: it is
/// evidence. It is also the only part of the error the API exposes — there is
/// no category field and no error timestamp on the project resource — so
/// [footnote] states that rather than letting the panel imply more precision
/// than exists.
///
/// Nothing here can display a stack trace, a header or a credential: the panel
/// has exactly one text field and the caller has exactly one string.
class RecordedErrorPanel extends StatelessWidget {
  const RecordedErrorPanel({
    required this.title,
    required this.message,
    this.footnote,
    super.key,
  });

  /// Localized heading.
  final String title;

  /// The server's own message, rendered as it arrived.
  final String message;

  /// Localized note about what the API does *not* say about this error.
  final String? footnote;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final onContainer = theme.colorScheme.onErrorContainer;

    return Semantics(
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
              children: [
                Icon(Icons.error_outline, size: 18, color: onContainer),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    title,
                    style: theme.textTheme.labelLarge?.copyWith(
                      color: onContainer,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: AppSpacing.sm),
            // Selectable, because the next thing an operator does with a
            // backend error is paste it somewhere.
            SelectableText(
              message,
              style: theme.textTheme.bodySmall?.copyWith(color: onContainer),
            ),
            if (footnote != null) ...[
              const SizedBox(height: AppSpacing.sm),
              Text(
                footnote!,
                style: theme.textTheme.labelSmall?.copyWith(
                  color: onContainer.withValues(alpha: 0.8),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// The screen state for a project the API answered `404` for.
///
/// A missing project is not a server failure: retrying the same request will
/// keep returning `404`, so the way out is the list, not a Retry button.
class NotFoundView extends StatelessWidget {
  const NotFoundView({
    required this.title,
    required this.message,
    required this.actionLabel,
    required this.onAction,
    super.key,
  });

  final String title;
  final String message;
  final String actionLabel;
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.search_off,
              size: 40,
              color: theme.colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: AppSpacing.md),
            Text(
              title,
              style: theme.textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.sm),
            Text(
              message,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.md),
            FilledButton.tonalIcon(
              onPressed: onAction,
              icon: const Icon(Icons.arrow_back),
              label: Text(actionLabel),
            ),
          ],
        ),
      ),
    );
  }
}
