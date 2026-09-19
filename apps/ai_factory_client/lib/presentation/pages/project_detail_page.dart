import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/localization/relative_time.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/models/project.dart';
import '../controllers/project_detail_controller.dart';
import '../widgets/panels.dart';
import '../widgets/state_views.dart';
import '../widgets/status_chip.dart';

/// Project Detail — **a placeholder, and it says so.**
///
/// It shows only what the Projects list already showed, read back from
/// `GET /api/projects/{id}` so a deep link works: identity, the status the
/// server holds, when it was created and last updated, and the error the
/// backend recorded if there is one.
///
/// Everything that makes this screen useful — the lifecycle visualization,
/// work items and their dependency tree, agent runs, acceptance, artifacts,
/// human review and approval — belongs to Phase 19.4 and later. None of it is
/// imitated here: an unfinished screen that looks finished is worse than an
/// honest one, because an operator would trust it.
class ProjectDetailPage extends StatelessWidget {
  const ProjectDetailPage({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = Get.find<ProjectDetailController>();

    return Scaffold(
      appBar: AppBar(
        title: Obx(() {
          final project = controller.state.value.dataOrNull;
          return Text(project?.name ?? 'project.title'.tr());
        }),
        actions: [
          Obx(
            () => IconButton(
              onPressed: controller.state.value.isBusy
                  ? null
                  : controller.refreshData,
              tooltip: 'common.refresh'.tr(),
              icon: const Icon(Icons.refresh),
            ),
          ),
        ],
      ),
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(
              maxWidth: AppSpacing.maxContentWidth,
            ),
            child: Padding(
              padding: const EdgeInsets.all(AppSpacing.pageGutter),
              child: Obx(() {
                final state = controller.state.value;

                return Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (state is Refreshing<Project>)
                      const LinearProgressIndicator(minHeight: 2),
                    Expanded(
                      child: AsyncView<Project>(
                        state: state,
                        onRetry: controller.load,
                        builder: (context, project) =>
                            _ProjectDetailBody(project: project),
                      ),
                    ),
                  ],
                );
              }),
            ),
          ),
        ),
      ),
    );
  }
}

class _ProjectDetailBody extends StatelessWidget {
  const _ProjectDetailBody({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    return ListView(
      children: [
        _Header(project: project),
        if (project.hasError) ...[
          const SizedBox(height: AppSpacing.md),
          _CurrentErrorPanel(message: project.currentError!),
        ],
        const SizedBox(height: AppSpacing.md),
        _NextPhaseNotice(),
      ],
    );
  }
}

class _Header extends StatelessWidget {
  const _Header({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = context.locale.toString();

    return SectionCard(
      title: project.name,
      subtitle: project.description,
      child: ResponsiveBuilder(
        builder: (context, sizeClass) {
          final facts = <Widget>[
            _Fact(
              label: 'projects.field.status'.tr(),
              child: StatusChip.project(
                project.status,
                rawStatus: project.rawStatus,
              ),
            ),
            _Fact(
              label: 'project.field.identifier'.tr(),
              // The id is operator data and is never translated or shortened;
              // it is the join key into the audit log.
              child: SelectableText(
                project.id,
                style: theme.textTheme.bodySmall,
              ),
            ),
            _Fact(
              label: 'projects.field.created'.tr(),
              child: Text(
                RelativeTime.absolute(project.createdAtUtc, locale),
                style: theme.textTheme.bodySmall,
              ),
            ),
            _Fact(
              label: 'projects.field.updated'.tr(),
              child: Text(
                RelativeTime.absolute(project.updatedAtUtc, locale),
                style: theme.textTheme.bodySmall,
              ),
            ),
            _Fact(
              label: 'project.field.started'.tr(),
              child: Text(
                project.lifecycleStartedAtUtc == null
                    ? 'project.notStarted'.tr()
                    : RelativeTime.absolute(
                        project.lifecycleStartedAtUtc!,
                        locale,
                      ),
                style: theme.textTheme.bodySmall,
              ),
            ),
          ];

          return Wrap(
            spacing: AppSpacing.xl,
            runSpacing: AppSpacing.md,
            children: [
              for (final fact in facts)
                SizedBox(width: sizeClass.isCompact ? double.infinity : 220, child: fact),
            ],
          );
        },
      ),
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, required this.child});

  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label,
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        Align(alignment: Alignment.centerLeft, child: child),
      ],
    );
  }
}

/// `currentError` as the backend recorded it. Server text, shown to an
/// operator in an operator's screen — not a stack trace, and not a message
/// this client wrote on the server's behalf.
class _CurrentErrorPanel extends StatelessWidget {
  const _CurrentErrorPanel({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
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
              Icon(
                Icons.error_outline,
                size: 18,
                color: theme.colorScheme.onErrorContainer,
              ),
              const SizedBox(width: AppSpacing.sm),
              Text(
                'project.currentError'.tr(),
                style: theme.textTheme.labelLarge?.copyWith(
                  color: theme.colorScheme.onErrorContainer,
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          SelectableText(
            message,
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onErrorContainer,
            ),
          ),
        ],
      ),
    );
  }
}

class _NextPhaseNotice extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: 'project.placeholder.title'.tr(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'project.placeholder.message'.tr(),
            style: Theme.of(context).textTheme.bodyMedium,
          ),
          const SizedBox(height: AppSpacing.md),
          UnavailableNote(
            title: 'project.placeholder.plannedTitle'.tr(),
            reason: 'project.placeholder.planned'.tr(),
          ),
        ],
      ),
    );
  }
}
