import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../controllers/agent_runs_controller.dart';
import '../controllers/human_review_controller.dart';
import '../controllers/work_items_controller.dart';
import '../dialogs/approval_confirmation_dialog.dart';
import '../models/agent_runs_data.dart';
import '../models/work_items_data.dart';
import 'panels.dart';

/// The Human Review section of Project Detail.
///
/// Visibility rules — renders only when the project's status warrants it:
///
/// * `HumanReview` → full section with summary, evidence and the Approve
///    button. This is the one state where approval is legally possible.
/// * `Completed` → compact "Project Completed" note. Approval already
///    happened and the Approve button is absent.
/// * Anything else → `SizedBox.shrink()`. The section must not appear on
///    projects that are still in the automated pipeline.
///
/// The section reads state from the three sibling controllers already on the
/// route — it does not issue any HTTP requests of its own.
class HumanReviewSection extends StatelessWidget {
  const HumanReviewSection({
    required this.projectStatus,
    required this.projectName,
    required this.projectError,
    super.key,
  });

  final ProjectStatus projectStatus;

  /// The project's own name — shown in the confirmation dialog so the
  /// operator knows exactly which project they are approving.
  final String projectName;

  /// `currentError` from the project — forwarded to the summary panel.
  final String? projectError;

  @override
  Widget build(BuildContext context) {
    return switch (projectStatus) {
      ProjectStatus.humanReview => _HumanReviewBody(
        projectName: projectName,
        projectError: projectError,
      ),
      ProjectStatus.completed => _CompletedNote(),
      _ => const SizedBox.shrink(),
    };
  }
}

// ---------------------------------------------------------------------------
// HumanReview body
// ---------------------------------------------------------------------------

class _HumanReviewBody extends StatelessWidget {
  const _HumanReviewBody({
    required this.projectName,
    required this.projectError,
  });

  final String projectName;
  final String? projectError;

  @override
  Widget build(BuildContext context) {
    // Register locale dependency so the section re-builds on language switch,
    // matching the precedent in ProjectDetailPage (§9.5 of flutter-architecture.md).
    final _ = context.locale;

    final controller = Get.find<HumanReviewController>();

    return Obx(() {
      final approvalError = controller.approvalError.value;

      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Approval error banner — separate from the section content so a
          // failed approval never blanks out the evidence the operator needs.
          if (approvalError != null) ...[
            ActionErrorBanner(
              error: approvalError,
              onDismiss: controller.dismissApprovalError,
            ),
            const SizedBox(height: AppSpacing.md),
          ],
          SectionCard(
            title: 'humanReview.title'.tr(),
            subtitle: 'humanReview.subtitle'.tr(),
            trailing: _ApproveButton(
              projectName: projectName,
              controller: controller,
            ),
            child: ResponsiveBuilder(
              builder: (context, sizeClass) => sizeClass.isCompact
                  ? _CompactLayout(projectError: projectError)
                  : _ExpandedLayout(projectError: projectError),
            ),
          ),
        ],
      );
    });
  }
}

// ---------------------------------------------------------------------------
// Approve button
// ---------------------------------------------------------------------------

class _ApproveButton extends StatelessWidget {
  const _ApproveButton({required this.projectName, required this.controller});

  final String projectName;
  final HumanReviewController controller;

  @override
  Widget build(BuildContext context) {
    return Obx(() {
      final isApproving = controller.isApproving.value;
      // The consequence is read out on the button's own node — the one that
      // takes keyboard focus — not on a wrapper around it.
      return FilledButton.icon(
        onPressed: isApproving ? null : () => _onTap(context),
        icon: isApproving
            ? const SizedBox(
                width: 16,
                height: 16,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.check_circle_outline, size: 18),
        label: isApproving
            ? Text('humanReview.approvePending'.tr())
            : Semantics(
                label: 'humanReview.approveButtonSemantics'.tr(),
                excludeSemantics: true,
                child: Text('humanReview.approveButton'.tr()),
              ),
      );
    });
  }

  Future<void> _onTap(BuildContext context) async {
    final confirmed = await ApprovalConfirmationDialog.show(
      context,
      projectName,
    );
    if (!confirmed) return;
    await controller.approveProject();
  }
}

// ---------------------------------------------------------------------------
// Layouts
// ---------------------------------------------------------------------------

class _CompactLayout extends StatelessWidget {
  const _CompactLayout({required this.projectError});

  final String? projectError;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _ReviewSummaryPanel(projectError: projectError),
        const SizedBox(height: AppSpacing.md),
        const _ReviewEvidencePanel(),
      ],
    );
  }
}

class _ExpandedLayout extends StatelessWidget {
  const _ExpandedLayout({required this.projectError});

  final String? projectError;

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(child: _ReviewSummaryPanel(projectError: projectError)),
        const SizedBox(width: AppSpacing.md),
        const Expanded(child: _ReviewEvidencePanel()),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// Review Summary panel — work items, errors, acceptance
// ---------------------------------------------------------------------------

class _ReviewSummaryPanel extends StatelessWidget {
  const _ReviewSummaryPanel({required this.projectError});

  final String? projectError;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'humanReview.summary.title'.tr(),
              style: theme.textTheme.titleSmall,
            ),
            const SizedBox(height: AppSpacing.md),

            // Work items from the sibling controller — already loaded.
            Obx(() {
              final workItems =
                  Get.find<WorkItemsController>().state.value.dataOrNull;
              return _WorkItemsSummary(data: workItems);
            }),

            const SizedBox(height: AppSpacing.md),

            // Project-level error field.
            Text(
              'humanReview.summary.errors'.tr(),
              style: theme.textTheme.labelMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: AppSpacing.xs),
            projectError != null
                ? Text(
                    projectError!,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.error,
                    ),
                  )
                : Text(
                    'humanReview.summary.noErrors'.tr(),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),

            const SizedBox(height: AppSpacing.md),

            // Acceptance: API gap G-ACCEPTANCE — explicitly unavailable.
            UnavailableNote(
              title: 'humanReview.summary.acceptance'.tr(),
              reason: 'humanReview.summary.acceptanceUnavailable'.tr(),
            ),
          ],
        ),
      ),
    );
  }
}

class _WorkItemsSummary extends StatelessWidget {
  const _WorkItemsSummary({required this.data});

  final WorkItemsData? data;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    if (data == null) {
      return Text(
        'common.loading'.tr(),
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      );
    }

    final items = data!.workItems;
    final completed = items
        .where((i) => i.status == WorkItemStatus.completed)
        .length;
    final failed = items
        .where(
          (i) =>
              i.status == WorkItemStatus.failed ||
              i.status == WorkItemStatus.cancelled,
        )
        .length;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'humanReview.summary.workItems'.tr(),
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(
          'humanReview.summary.completed'.tr(args: ['$completed']),
          style: theme.textTheme.bodySmall,
        ),
        if (failed > 0)
          Text(
            'humanReview.summary.failed'.tr(args: ['$failed']),
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.error,
            ),
          ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// Review Evidence panel — agent runs, structured results, telemetry
// ---------------------------------------------------------------------------

class _ReviewEvidencePanel extends StatelessWidget {
  const _ReviewEvidencePanel();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'humanReview.evidence.title'.tr(),
              style: theme.textTheme.titleSmall,
            ),
            const SizedBox(height: AppSpacing.md),

            // Agent runs from the sibling controller — already loaded.
            Obx(() {
              final runsData =
                  Get.find<AgentRunsController>().state.value.dataOrNull;
              return _AgentRunsSummary(data: runsData);
            }),

            const SizedBox(height: AppSpacing.md),

            // Structured results — note only; details are in AgentRunsSection above.
            Text(
              'humanReview.evidence.structuredResults'.tr(),
              style: theme.textTheme.labelMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: AppSpacing.xs),
            Text(
              'humanReview.evidence.structuredResultsNote'.tr(),
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),

            const SizedBox(height: AppSpacing.md),

            // Telemetry: API gap G-RUN-TELEMETRY — explicitly unavailable.
            UnavailableNote(
              title: 'humanReview.evidence.telemetry'.tr(),
              reason: 'humanReview.evidence.telemetryNote'.tr(),
            ),
          ],
        ),
      ),
    );
  }
}

class _AgentRunsSummary extends StatelessWidget {
  const _AgentRunsSummary({required this.data});

  final AgentRunsData? data;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    if (data == null) {
      return Text(
        'common.loading'.tr(),
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      );
    }

    final runs = data!.runs;
    final total = data!.totalCount;
    final succeeded = runs
        .where((r) => r.status == AgentRunStatus.succeeded)
        .length;
    final failed = runs
        .where(
          (r) =>
              r.status == AgentRunStatus.failed ||
              r.status == AgentRunStatus.timedOut ||
              r.status == AgentRunStatus.cancelled,
        )
        .length;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'humanReview.evidence.agentRuns'.tr(),
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(
          'humanReview.evidence.agentRunsSummary'.tr(
            args: ['$total', '$succeeded', '$failed'],
          ),
          style: theme.textTheme.bodySmall,
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// Completed note
// ---------------------------------------------------------------------------

class _CompletedNote extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final _ = context.locale;
    final theme = Theme.of(context);

    return SectionCard(
      title: 'humanReview.completed.title'.tr(),
      child: Text(
        'humanReview.completed.body'.tr(),
        style: theme.textTheme.bodyMedium?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}
