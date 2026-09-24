import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../app/app_routes.dart';
import '../../core/responsive/breakpoints.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../controllers/agent_runs_controller.dart';
import '../controllers/project_detail_controller.dart';
import '../controllers/work_items_controller.dart';
import '../models/project_lifecycle.dart';
import '../models/project_source.dart';
import '../widgets/agent_runs_section.dart';
import '../widgets/error_panel.dart';
import '../widgets/human_review_section.dart';
import '../widgets/live_updates.dart';
import '../widgets/panels.dart';
import '../widgets/project_header.dart';
import '../widgets/project_lifecycle_widget.dart';
import '../widgets/state_views.dart';
import '../widgets/work_items_section.dart';

/// Project Detail — one project, as the API currently describes it.
///
/// The screen answers three questions and refuses to answer a fourth:
///
/// 1. *What is this project?* — identity, source, description, timestamps.
/// 2. *Where is it in its lifecycle?* — the backend transition table with the
///    reported state marked, and an explicit statement that stage history is
///    not exposed.
/// 3. *What can I do about it?* — Start, Refresh, Back, and nothing the API
///    does not actually support.
///
/// The fourth question — *how is the work going, and can it be signed off?* —
/// is answered by the work items, agent runs and Human Review sections below,
/// each scoped to exactly what the API actually returns: a panel that looks
/// finished is trusted, so an unfinished one is worse than an absent one.
class ProjectDetailPage extends StatelessWidget {
  const ProjectDetailPage({super.key});

  @override
  Widget build(BuildContext context) {
    final controller = Get.find<ProjectDetailController>();

    // `.tr()` reads a static table, so nothing rebuilds on a language switch
    // unless it asks. `AppShell` registers that dependency for the screens
    // inside it; this screen is a pushed route above the shell, so it has to
    // register its own — otherwise a widget that touches no other inherited
    // value (`ProjectLifecycleWidget`'s section titles, for instance) keeps
    // the language it was first built in.
    // See `docs/flutter-architecture.md` §9.5.
    final _ = context.locale;

    return LiveUpdatesScope(
      poller: controller.live,
      child: Scaffold(
        appBar: AppBar(
          leading: BackButton(onPressed: () => _backToProjects()),
          title: Obx(() {
            // The project's own name once it is known; the generic screen title
            // until then, never a blank bar.
            final project = controller.state.value.dataOrNull;
            return Text(project?.name ?? 'projectDetail.title'.tr());
          }),
          actions: [
            // Operational diagnostics for this project. A separate screen with
            // its own controller: it re-reads by id rather than being handed
            // this screen's data (Workstream M).
            Obx(() {
              final project = controller.state.value.dataOrNull;
              return IconButton(
                onPressed: project == null
                    ? null
                    : () => Get.toNamed<void>(
                        AppRoutes.projectDiagnostics(project.id),
                      ),
                tooltip: 'diagnostics.open'.tr(),
                icon: const Icon(Icons.monitor_heart_outlined),
              );
            }),
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
                  final actionError = controller.actionError.value;

                  // A missing project is its own state, not a server failure:
                  // retrying a 404 returns a 404.
                  if (controller.isNotFound) {
                    return NotFoundView(
                      title: 'projectDetail.notFound.title'.tr(),
                      message: 'projectDetail.notFound.message'.tr(),
                      actionLabel: 'projectDetail.backToProjects'.tr(),
                      onAction: _backToProjects,
                    );
                  }

                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      LiveStatusIndicator(poller: controller.live),
                      const SizedBox(height: AppSpacing.sm),
                      // A manual refresh keeps the project on screen and adds a
                      // thin line; it never blanks the page. A background poll
                      // shows nothing but the indicator above.
                      if (state is Refreshing<Project>)
                        const LinearProgressIndicator(minHeight: 2),
                      if (actionError != null) ...[
                        ActionErrorBanner(
                          error: actionError,
                          onDismiss: controller.dismissActionError,
                        ),
                        const SizedBox(height: AppSpacing.md),
                      ],
                      Expanded(
                        child: AsyncView<Project>(
                          state: state,
                          onRetry: controller.load,
                          builder: (context, project) => _ProjectDetailBody(
                            project: project,
                            controller: controller,
                          ),
                        ),
                      ),
                    ],
                  );
                }),
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Back to Projects, whether this screen was pushed from the list or opened
  /// directly by URL. A deep link has nothing to pop, so it navigates instead
  /// of leaving the operator on a screen with no way out.
  static void _backToProjects() {
    if (Get.key.currentState?.canPop() ?? false) {
      Get.back<void>();
      return;
    }
    Get.offNamed<void>(AppRoutes.projects);
  }
}

class _ProjectDetailBody extends StatelessWidget {
  const _ProjectDetailBody({required this.project, required this.controller});

  final Project project;
  final ProjectDetailController controller;

  @override
  Widget build(BuildContext context) {
    final overview = _OverviewSection(project: project);
    final source = _SourceSection(project: project);

    return ListView(
      children: [
        Obx(
          () => ProjectHeaderPanel(
            project: project,
            onStart: controller.canStart
                ? () => controller.startProject()
                : null,
            isStarting: controller.isStarting.value,
            startUnavailableReason: _startUnavailableReason(project),
          ),
        ),
        if (project.hasError) ...[
          const SizedBox(height: AppSpacing.md),
          RecordedErrorPanel(
            title: 'project.currentError'.tr(),
            message: project.currentError!,
            footnote: 'projectDetail.error.noCategory'.tr(),
          ),
        ],
        const SizedBox(height: AppSpacing.md),
        ProjectLifecycleWidget(lifecycle: ProjectLifecycle.of(project)),
        const SizedBox(height: AppSpacing.md),
        // Two narrow sections sit side by side from tablet width upwards and
        // stack below it — the same two widgets either way.
        ResponsiveBuilder(
          builder: (context, sizeClass) => sizeClass.isCompact
              ? Column(
                  children: [
                    overview,
                    const SizedBox(height: AppSpacing.md),
                    source,
                  ],
                )
              // Top-aligned rather than stretched: equalizing the two card
              // heights would mean an intrinsic-height pass, and the cards
              // contain a LayoutBuilder, which cannot answer one.
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(child: overview),
                    const SizedBox(width: AppSpacing.md),
                    Expanded(child: source),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.md),
        Builder(
          builder: (context) {
            final workItems = Get.find<WorkItemsController>();
            final agentRuns = Get.find<AgentRunsController>();
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                WorkItemsSection(
                  controller: workItems,
                  onViewAgentRuns: agentRuns.filterByWorkItem,
                ),
                const SizedBox(height: AppSpacing.md),
                Obx(() {
                  final workItemTitleById = <String, String>{
                    for (final item
                        in workItems.state.value.dataOrNull?.workItems ??
                            const [])
                      item.id: item.title,
                  };
                  return AgentRunsSection(
                    controller: agentRuns,
                    workItemTitleById: workItemTitleById,
                    onViewWorkItem: workItems.selectWorkItem,
                  );
                }),
                // Phase 19.7 — Human Review and explicit approval.
                // Self-managing: renders only for HumanReview and Completed;
                // SizedBox.shrink() for all other statuses.
                const SizedBox(height: AppSpacing.md),
                Obx(() {
                  // Re-read from the detail controller so the section
                  // immediately reflects a status change from approval.
                  final project = controller.state.value.dataOrNull;
                  if (project == null) return const SizedBox.shrink();
                  return HumanReviewSection(
                    projectStatus: project.status,
                    projectName: project.name,
                    projectError: project.currentError,
                  );
                }),
              ],
            );
          },
        ),
      ],
    );
  }

  /// Why Start is not offered, when the reason is not obvious from the screen.
  ///
  /// A project still in `Created` has no initialized source, and the backend
  /// rejects a start for exactly that reason — so the screen says so instead
  /// of showing a button that is guaranteed to fail. A project that has
  /// already started needs no explanation: its "Lifecycle started" timestamp
  /// is the explanation.
  static String? _startUnavailableReason(Project project) {
    if (project.isStarted) return null;
    if (project.status == ProjectStatus.created) {
      return 'projectDetail.start.sourceRequired'.tr();
    }
    return null;
  }
}

/// The operator's own text about the project.
///
/// Never translated, never summarized, never truncated: it is the input the
/// Factory plans against, and an operator comparing it with what was built
/// needs the whole of it.
class _OverviewSection extends StatelessWidget {
  const _OverviewSection({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final description = project.description?.trim();
    final hasDescription = description != null && description.isNotEmpty;

    return SectionCard(
      title: 'projectDetail.overview.title'.tr(),
      child: Align(
        alignment: Alignment.centerLeft,
        child: hasDescription
            ? SelectableText(description, style: theme.textTheme.bodyMedium)
            : Text(
                'projectDetail.overview.empty'.tr(),
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
      ),
    );
  }
}

/// Where the project's code comes from, to the extent the API says.
///
/// `GET /api/projects/{id}` returns `gitRepository` and `gitBranch` and no
/// source-type field, so the kind is derived from those two and nothing more
/// is claimed. Any credentials embedded in the repository URL are stripped
/// before it reaches the widget tree — see
/// [ProjectSourceView.redactCredentials].
class _SourceSection extends StatelessWidget {
  const _SourceSection({required this.project});

  final Project project;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final source = ProjectSourceView.of(project);

    final kindLabel = switch (source.kind) {
      ProjectSourceKind.notInitialized => 'source.kind.notInitialized'.tr(),
      ProjectSourceKind.empty => 'source.kind.empty'.tr(),
      ProjectSourceKind.gitRepository => 'source.kind.git'.tr(),
    };

    final note = switch (source.kind) {
      ProjectSourceKind.notInitialized => 'source.notInitializedHelp'.tr(),
      ProjectSourceKind.empty => 'source.emptyHelp'.tr(),
      ProjectSourceKind.gitRepository => null,
    };

    return SectionCard(
      title: 'source.title'.tr(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          FactList(
            facts: <Fact>[
              Fact(
                label: 'source.field.kind'.tr(),
                child: Text(kindLabel, style: theme.textTheme.bodyMedium),
              ),
              if (source.isGit) ...[
                Fact(
                  label: 'source.field.repository'.tr(),
                  // Operator data: a URL is shown as it was recorded, minus
                  // any embedded credential.
                  child: SelectableText(
                    source.repository!,
                    style: theme.textTheme.bodySmall,
                  ),
                ),
                Fact(
                  label: 'source.field.branch'.tr(),
                  child: source.branch == null
                      ? Text(
                          'source.branchDefault'.tr(),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        )
                      : SelectableText(
                          source.branch!,
                          style: theme.textTheme.bodySmall,
                        ),
                ),
              ],
            ],
          ),
          if (note != null) ...[
            const SizedBox(height: AppSpacing.md),
            Text(
              note,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
    );
  }
}
