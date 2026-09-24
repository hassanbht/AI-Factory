import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/localization/enum_labels.dart';
import '../../core/theme/app_spacing.dart';
import '../../core/utils/formatters.dart';
import '../../domain/models/agent_run.dart';
import 'error_panel.dart';
import 'panels.dart';
import 'status_chip.dart';
import 'timestamp_text.dart';

/// The selected agent run, in full: exactly the fields `AgentRunDto` returns
/// (`docs/flutter-api-contract.md` §6, `docs/flutter-ux-spec.md` §6.6) and
/// nothing more. `model`, `provider` and fallback information are persisted
/// on the backend entity but never reach this endpoint (gaps
/// `G-RUN-TELEMETRY`, `G-FALLBACK`), so that area states its own absence
/// instead of a blank or a guess. API keys, tokens and workspace paths have
/// no field on this panel at all — there is nothing to accidentally render.
class AgentRunDetailPanel extends StatelessWidget {
  const AgentRunDetailPanel({
    required this.run,
    this.agentLabel,
    this.agentTypeLabel,
    this.workItemTitle,
    this.onViewWorkItem,
    super.key,
  });

  final AgentRun run;
  final String? agentLabel;
  final String? agentTypeLabel;

  /// The related work item's title, already resolved by the caller — this
  /// panel never looks it up itself, so it stays reusable when the join
  /// cannot be made (a run whose work item is not in the loaded page).
  final String? workItemTitle;
  final VoidCallback? onViewWorkItem;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = context.locale.toString();

    return SectionCard(
      title: agentLabel ?? 'agentRun.unknownAgent'.tr(args: [run.id]),
      subtitle: agentTypeLabel,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          FactList(
            facts: <Fact>[
              Fact(
                label: 'agentRun.field.status'.tr(),
                child: StatusChip.agentRun(
                  run.status,
                  rawStatus: run.rawStatus,
                ),
              ),
              Fact(
                label: 'agentRun.field.attempt'.tr(),
                child: Text(
                  '${run.attempt}',
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              Fact(
                label: 'agentRun.field.project'.tr(),
                child: SelectableText(
                  run.projectId ?? 'common.notAvailable'.tr(),
                  style: theme.textTheme.bodySmall,
                ),
              ),
              Fact(
                label: 'agentRun.field.workItem'.tr(),
                child: run.workItemId == null
                    ? Text(
                        'agentRun.noWorkItem'.tr(),
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      )
                    : Wrap(
                        crossAxisAlignment: WrapCrossAlignment.center,
                        spacing: AppSpacing.sm,
                        children: [
                          Text(
                            workItemTitle ?? run.workItemId!,
                            style: theme.textTheme.bodyMedium,
                          ),
                          if (onViewWorkItem != null)
                            TextButton(
                              onPressed: onViewWorkItem,
                              child: Text('agentRun.action.viewWorkItem'.tr()),
                            ),
                        ],
                      ),
              ),
              Fact(
                label: 'agentRun.field.created'.tr(),
                child: TimestampText(timestamp: run.createdAtUtc),
              ),
              Fact(
                label: 'agentRun.field.started'.tr(),
                child: TimestampText(timestamp: run.startedAtUtc),
              ),
              Fact(
                label: 'agentRun.field.completed'.tr(),
                child: TimestampText(timestamp: run.completedAtUtc),
              ),
              Fact(
                label: 'agentRun.field.duration'.tr(),
                child: Text(
                  run.duration == null
                      ? 'common.notAvailable'.tr()
                      : Formatters.duration(run.duration!, locale),
                  style: theme.textTheme.bodyMedium,
                ),
              ),
              if (run.failureCategory != null)
                Fact(
                  label: 'agentRun.field.failureCategory'.tr(),
                  child: Text(
                    EnumLabels.failureCategory(
                      run.failureCategory!,
                      rawValue: run.rawFailureCategory,
                    ),
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
            ],
          ),
          const SizedBox(height: AppSpacing.md),
          Fact(
            label: 'agentRun.field.correlationId'.tr(),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Flexible(
                  child: SelectableText(
                    run.correlationId,
                    style: theme.textTheme.bodySmall,
                  ),
                ),
                IconButton(
                  onPressed: () => _copyCorrelationId(context),
                  tooltip: 'agentRun.action.copyCorrelationId'.tr(),
                  icon: const Icon(Icons.copy, size: 16),
                  visualDensity: VisualDensity.compact,
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          UnavailableNote(
            title: 'agentRun.telemetry.title'.tr(),
            reason: 'agentRun.telemetry.reason'.tr(),
          ),
          if (run.errorMessage != null) ...[
            const SizedBox(height: AppSpacing.md),
            RecordedErrorPanel(
              title: 'agentRun.field.currentError'.tr(),
              message: run.errorMessage!,
              footnote: run.failureCategory == null
                  ? 'workItem.error.noCategory'.tr()
                  : null,
            ),
          ],
          const SizedBox(height: AppSpacing.md),
          _StructuredResultSection(result: run.structuredResult),
        ],
      ),
    );
  }

  Future<void> _copyCorrelationId(BuildContext context) async {
    await Clipboard.setData(ClipboardData(text: run.correlationId));
    if (!context.mounted) return;
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text('common.copied'.tr())));
  }
}

/// `structuredResult` — only the sections a real value actually carries.
/// Workstream I: never interpret arbitrary agent text as structured truth,
/// so every section here comes straight off `StructuredAgentResultDto` and
/// nothing is inferred from `output` or `errorMessage`.
class _StructuredResultSection extends StatelessWidget {
  const _StructuredResultSection({required this.result});

  final StructuredAgentResult? result;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final data = result;

    if (data == null) {
      return UnavailableNote(
        title: 'agentRun.structuredResult.title'.tr(),
        reason: 'agentRun.structuredResult.none'.tr(),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'agentRun.structuredResult.title'.tr(),
          style: theme.textTheme.titleSmall,
        ),
        const SizedBox(height: AppSpacing.sm),
        FactList(
          facts: <Fact>[
            Fact(
              label: 'agentRun.field.status'.tr(),
              child: Text(
                EnumLabels.structuredResultStatus(
                  data.status,
                  rawValue: data.rawStatus,
                ),
                style: theme.textTheme.bodyMedium,
              ),
            ),
          ],
        ),
        if (data.summary != null && data.summary!.trim().isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.summary'.tr(),
            child: SelectableText(
              data.summary!,
              style: theme.textTheme.bodyMedium,
            ),
          ),
        ],
        if (data.output != null && data.output!.trim().isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.output'.tr(),
            child: SelectableText(
              data.output!,
              style: theme.textTheme.bodySmall,
            ),
          ),
        ],
        if (data.data != null && data.data!.trim().isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.data'.tr(),
            child: SelectableText(data.data!, style: theme.textTheme.bodySmall),
          ),
        ],
        if (data.hasWarnings) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.warnings'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (final warning in data.warnings)
                  Text('• $warning', style: theme.textTheme.bodySmall),
              ],
            ),
          ),
        ],
        if (data.hasErrors) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.errors'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (final error in data.errors)
                  Text('• $error', style: theme.textTheme.bodySmall),
              ],
            ),
          ),
        ],
        if (data.artifacts.isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.artifacts'.tr(),
            child: Wrap(
              spacing: AppSpacing.sm,
              runSpacing: AppSpacing.xs,
              children: [
                for (final artifact in data.artifacts)
                  Chip(
                    label: Text(artifact),
                    visualDensity: VisualDensity.compact,
                  ),
              ],
            ),
          ),
        ],
        if (data.metadata != null && data.metadata!.isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Fact(
            label: 'agentRun.structuredResult.metadata'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (final entry in data.metadata!.entries)
                  Text(
                    '${entry.key}: ${entry.value}',
                    style: theme.textTheme.bodySmall,
                  ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}
