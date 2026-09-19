import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/errors/app_error.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';

/// Loading, empty and error presentations shared by every screen.
///
/// They exist in the foundation so no later phase invents its own — and so
/// the rule "never show a blank screen, never show a stack trace" is enforced
/// by the only widgets available for the job.

class LoadingView extends StatelessWidget {
  const LoadingView({this.message, super.key});

  final String? message;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      liveRegion: true,
      label: 'state.loading.title'.tr(),
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(
              width: 28,
              height: 28,
              child: CircularProgressIndicator(strokeWidth: 3),
            ),
            const SizedBox(height: AppSpacing.md),
            Text(
              message ?? 'state.loading.message'.tr(),
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ],
        ),
      ),
    );
  }
}

class EmptyView extends StatelessWidget {
  const EmptyView({this.title, this.message, this.icon, super.key});

  final String? title;
  final String? message;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              icon ?? Icons.inbox_outlined,
              size: 40,
              color: theme.colorScheme.outline,
            ),
            const SizedBox(height: AppSpacing.md),
            Text(
              title ?? 'state.empty.title'.tr(),
              style: theme.textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.sm),
            Text(
              message ?? 'state.empty.message'.tr(),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
          ],
        ),
      ),
    );
  }
}

/// Renders an [AppError] as a localized message plus an optional, collapsed
/// technical block for the operator. Stack traces are never available here.
class ErrorView extends StatelessWidget {
  const ErrorView({
    required this.error,
    this.onRetry,
    this.showTechnicalDetails = true,
    super.key,
  });

  final AppError error;
  final VoidCallback? onRetry;
  final bool showTechnicalDetails;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final detail = error.technicalDetail;
    final correlationId = error.correlationId;
    final hasDetails =
        showTechnicalDetails && (detail != null || correlationId != null);

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 520),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            Icon(Icons.error_outline, size: 40, color: theme.colorScheme.error),
            const SizedBox(height: AppSpacing.md),
            Text(
              'state.error.title'.tr(),
              style: theme.textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.sm),
            Text(
              error.messageKey.tr(),
              style: theme.textTheme.bodyMedium,
              textAlign: TextAlign.center,
            ),
            if (onRetry != null) ...[
              const SizedBox(height: AppSpacing.md),
              FilledButton.tonalIcon(
                onPressed: onRetry,
                icon: const Icon(Icons.refresh),
                label: Text('common.retry'.tr()),
              ),
            ],
            if (hasDetails) ...[
              const SizedBox(height: AppSpacing.md),
              ExpansionTile(
                title: Text(
                  'state.error.technicalDetails'.tr(),
                  style: theme.textTheme.labelLarge,
                ),
                tilePadding: EdgeInsets.zero,
                childrenPadding: const EdgeInsets.only(bottom: AppSpacing.sm),
                children: [
                  if (detail != null)
                    SelectableText(detail, style: theme.textTheme.bodySmall),
                  if (correlationId != null) ...[
                    const SizedBox(height: AppSpacing.xs),
                    SelectableText(
                      '${'state.error.correlationId'.tr()}: $correlationId',
                      style: theme.textTheme.bodySmall,
                    ),
                  ],
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Maps a [ViewState] onto the views above, so a screen only supplies the
/// success case.
class AsyncView<T> extends StatelessWidget {
  const AsyncView({
    required this.state,
    required this.builder,
    this.onRetry,
    this.emptyTitle,
    this.emptyMessage,
    super.key,
  });

  final ViewState<T> state;
  final Widget Function(BuildContext context, T data) builder;
  final VoidCallback? onRetry;
  final String? emptyTitle;
  final String? emptyMessage;

  @override
  Widget build(BuildContext context) {
    return state.map(
      idle: () => const LoadingView(),
      loading: () => const LoadingView(),
      refreshing: (data) => builder(context, data),
      success: (data) => builder(context, data),
      partial: (data, _) => builder(context, data),
      empty: () => EmptyView(title: emptyTitle, message: emptyMessage),
      failure: (error, stale) => stale != null
          ? builder(context, stale)
          : ErrorView(error: error, onRetry: onRetry),
    );
  }
}
