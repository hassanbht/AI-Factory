import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/localization/enum_labels.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/audit.dart';
import '../controllers/audit_controller.dart';
import 'operations_panels.dart';
import 'timestamp_text.dart';

/// The operational audit log, as the backend records it.
///
/// Nothing here is derived. An entry with no actor id shows no actor id, and
/// the entry's `outcome` is never merged into its `severity` — the backend
/// keeps a log level and a business result apart, and so does this list.
///
/// The severity filter is labelled as client-side because it is: the endpoint
/// accepts a `severity` parameter and then ignores it (`G-AUDIT-SEVERITY`),
/// so the filter narrows the loaded page rather than pretending to re-query.
class AuditLogSection extends StatelessWidget {
  const AuditLogSection({required this.controller, this.onEnterKey, super.key});

  final AuditController controller;
  final VoidCallback? onEnterKey;

  @override
  Widget build(BuildContext context) {
    return Obx(() {
      final state = controller.state.value;

      // Re-typed to Object so one section widget serves every Operations
      // panel: the body reads the controller directly and does not need the
      // page's element type.
      final erased = switch (state) {
        Idle() => const Idle<Object>(),
        Loading() => const Loading<Object>(),
        Refreshing(:final data) => Refreshing<Object>(data),
        Success(:final data) => Success<Object>(data),
        Partial(:final data, :final failures) => Partial<Object>(
          data,
          failures,
        ),
        Empty() => const Empty<Object>(),
        Failure(:final error, :final stale) => Failure<Object>(
          error,
          stale: stale,
        ),
      };

      return OperationsSection<Object>(
        title: 'audit.title'.tr(),
        subtitle: 'audit.subtitle'.tr(),
        state: erased,
        onRetry: controller.load,
        onEnterKey: onEnterKey,
        builder: (context, _) => _AuditBody(controller: controller),
      );
    });
  }
}

class _AuditBody extends StatelessWidget {
  const _AuditBody({required this.controller});

  final AuditController controller;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Obx(
          () => Row(
            children: [
              Text('audit.filter.severity'.tr()),
              const SizedBox(width: AppSpacing.sm),
              DropdownButton<AuditSeverity?>(
                value: controller.severityFilter.value,
                onChanged: controller.filterBySeverity,
                items: <DropdownMenuItem<AuditSeverity?>>[
                  DropdownMenuItem(child: Text('audit.filter.all'.tr())),
                  for (final value in AuditSeverity.values)
                    if (value != AuditSeverity.unknown)
                      DropdownMenuItem(
                        value: value,
                        child: Text(EnumLabels.auditSeverity(value)),
                      ),
                ],
              ),
            ],
          ),
        ),
        Text(
          'audit.filter.clientSideNote'.tr(),
          style: theme.textTheme.labelSmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.sm),
        Obx(() {
          final entries = controller.visibleEntries;
          if (entries.isEmpty) {
            return Padding(
              padding: const EdgeInsets.symmetric(vertical: AppSpacing.sm),
              child: Text(
                'audit.noMatches'.tr(),
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            );
          }

          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              for (final entry in entries) ...[
                AuditLogTile(entry: entry),
                const SizedBox(height: AppSpacing.xs),
              ],
            ],
          );
        }),
        const SizedBox(height: AppSpacing.sm),
        Obx(() {
          final page = controller.page.value;
          final loaded = controller.state.value.dataOrNull;
          return Row(
            children: [
              Expanded(
                child: Text(
                  'audit.page'.tr(
                    args: ['$page', '${loaded?.totalCount ?? 0}'],
                  ),
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
              ),
              TextButton(
                onPressed: page > 1 ? controller.previousPage : null,
                child: Text('common.previous'.tr()),
              ),
              TextButton(
                onPressed: (loaded?.hasMore ?? false)
                    ? controller.nextPage
                    : null,
                child: Text('common.next'.tr()),
              ),
            ],
          );
        }),
      ],
    );
  }
}

/// One audit entry. Severity is announced as a word, never only as a colour.
class AuditLogTile extends StatelessWidget {
  const AuditLogTile({required this.entry, super.key});

  final AuditLogEntry entry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final severityLabel = EnumLabels.auditSeverity(entry.severity);

    final color = switch (entry.severity) {
      AuditSeverity.critical || AuditSeverity.error => scheme.error,
      AuditSeverity.warning => scheme.tertiary,
      _ => scheme.onSurfaceVariant,
    };

    return Semantics(
      label: <String>[
        severityLabel,
        entry.eventType,
        entry.message,
        ?entry.outcome,
      ].join(', '),
      excludeSemantics: true,
      child: Container(
        padding: const EdgeInsets.all(AppSpacing.sm),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(AppRadius.sm),
          border: Border.all(color: scheme.outlineVariant),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Wrap(
              spacing: AppSpacing.sm,
              runSpacing: AppSpacing.xs,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(Icons.circle, size: 8, color: color),
                    const SizedBox(width: AppSpacing.xs),
                    Text(
                      severityLabel,
                      style: theme.textTheme.labelSmall?.copyWith(color: color),
                    ),
                  ],
                ),
                // The event type is a backend identifier, shown verbatim in
                // both languages.
                Text(
                  entry.eventType,
                  style: theme.textTheme.labelMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
                TimestampText(
                  timestamp: entry.timestampUtc,
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
            const SizedBox(height: AppSpacing.xs),
            Text(entry.message, style: theme.textTheme.bodySmall),
            const SizedBox(height: AppSpacing.xs),
            Wrap(
              spacing: AppSpacing.md,
              runSpacing: AppSpacing.xs,
              children: [
                Text(
                  'audit.actor'.tr(args: [entry.actorType]),
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                  ),
                ),
                if (entry.actorId != null)
                  Text(
                    entry.actorId!,
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
                if (entry.outcome != null)
                  Text(
                    'audit.outcome'.tr(args: [entry.outcome!]),
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
                if (entry.failureCategory != null)
                  Text(
                    EnumLabels.failureCategory(entry.failureCategory!),
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
