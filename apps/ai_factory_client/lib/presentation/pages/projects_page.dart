import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../app/app_routes.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/models/project.dart';
import '../controllers/projects_controller.dart';
import '../dialogs/create_project_dialog.dart';
import '../models/project_list_filter.dart';
import '../shell/app_shell.dart';
import '../shell/shell_destination.dart';
import '../widgets/live_updates.dart';
import '../widgets/panels.dart';
import '../widgets/project_list_tile.dart';
import '../widgets/state_views.dart';

/// Optional navigation arguments, used by the Dashboard tiles so a count can
/// open the list already filtered to the projects behind it.
class ProjectsPageArguments {
  const ProjectsPageArguments({required this.statusFilter});

  final ProjectStatusFilter statusFilter;
}

/// The project list.
///
/// `GET /api/projects` returns every project in one response, with no paging,
/// filtering or ordering parameters (gap `G-PROJECT-PAGING`). The list is
/// therefore virtualized and filtered client-side, and above
/// [ProjectsController.largeListThreshold] rows it says so rather than
/// letting the screen quietly become unusable.
class ProjectsPage extends StatefulWidget {
  const ProjectsPage({super.key});

  @override
  State<ProjectsPage> createState() => _ProjectsPageState();
}

class _ProjectsPageState extends State<ProjectsPage> {
  late final ProjectsController controller = Get.find<ProjectsController>();

  @override
  void initState() {
    super.initState();

    final arguments = Get.arguments;
    if (arguments is ProjectsPageArguments) {
      controller.setStatusFilter(arguments.statusFilter);
    }
  }

  Future<void> _create() async {
    final created = await showCreateProjectDialog(context);
    if (created != null) {
      await controller.onProjectCreated(created);
    }
  }

  void _open(Project project) {
    Get.toNamed<void>(AppRoutes.projectDetail(project.id));
  }

  @override
  Widget build(BuildContext context) {
    return LiveUpdatesScope(
      poller: controller.live,
      child: AppShell(
        destination: ShellDestination.projects,
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
        builder: (context) => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Obx(() {
              final state = controller.state.value;
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _Toolbar(
                    controller: controller,
                    filter: controller.filter.value,
                    onCreate: _create,
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  LiveStatusIndicator(poller: controller.live),
                  if (state is Refreshing<List<Project>>)
                    const LinearProgressIndicator(minHeight: 2),
                  if (controller.actionError.value case final error?) ...[
                    const SizedBox(height: AppSpacing.sm),
                    ActionErrorBanner(
                      error: error,
                      onDismiss: controller.dismissActionError,
                    ),
                  ],
                  if (controller.isLargeList) ...[
                    const SizedBox(height: AppSpacing.sm),
                    _UnpagedListNotice(total: controller.totalCount),
                  ],
                ],
              );
            }),
            const SizedBox(height: AppSpacing.sm),
            Expanded(
              child: Obx(() {
                final state = controller.state.value;
                // Read inside the Obx closure, not inside the child widget's
                // build: GetX only tracks observables touched while the closure
                // runs, so a filter change has to be observed here.
                final visible = controller.visibleProjects;

                return AsyncView<List<Project>>(
                  state: state,
                  onRetry: controller.load,
                  emptyTitle: 'projects.empty.title'.tr(),
                  emptyMessage: 'projects.empty.message'.tr(),
                  builder: (context, _) => _ProjectList(
                    controller: controller,
                    projects: visible,
                    onOpen: _open,
                  ),
                );
              }),
            ),
          ],
        ),
      ),
    );
  }
}

class _Toolbar extends StatelessWidget {
  const _Toolbar({
    required this.controller,
    required this.filter,
    required this.onCreate,
  });

  final ProjectsController controller;
  final ProjectListFilter filter;
  final VoidCallback onCreate;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: TextField(
                onChanged: controller.setQuery,
                textInputAction: TextInputAction.search,
                decoration: InputDecoration(
                  isDense: true,
                  prefixIcon: const Icon(Icons.search, size: 18),
                  hintText: 'projects.searchHint'.tr(),
                  labelText: 'projects.search'.tr(),
                  border: const OutlineInputBorder(),
                ),
              ),
            ),
            const SizedBox(width: AppSpacing.sm),
            FilledButton.icon(
              onPressed: onCreate,
              icon: const Icon(Icons.add, size: 18),
              label: Text('projects.action.create'.tr()),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.sm),
        SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(
            children: [
              for (final option in ProjectStatusFilter.values)
                Padding(
                  padding: const EdgeInsets.only(right: AppSpacing.sm),
                  child: ChoiceChip(
                    label: Text(option.labelKey.tr()),
                    selected: filter.status == option,
                    onSelected: (_) => controller.setStatusFilter(option),
                  ),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

class _ProjectList extends StatelessWidget {
  const _ProjectList({
    required this.controller,
    required this.projects,
    required this.onOpen,
  });

  final ProjectsController controller;
  final List<Project> projects;
  final void Function(Project) onOpen;

  @override
  Widget build(BuildContext context) {
    // The list loaded and has rows, but the filter hides all of them. That is
    // a different situation from "there are no projects", and saying so is
    // the difference between a usable screen and a confusing one.
    if (projects.isEmpty) {
      return _FilteredEmptyState(onClear: controller.clearFilter);
    }

    return Column(
      children: [
        const ProjectListHeader(),
        Expanded(
          child: ListView.separated(
            // Virtualized: only the visible rows are built, which is what
            // keeps an unpaged collection affordable.
            itemCount: projects.length,
            separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.sm),
            itemBuilder: (context, index) {
              final project = projects[index];
              return Obx(
                () => ProjectListTile(
                  project: project,
                  onOpen: () => onOpen(project),
                  onStart: ProjectListTile.canStart(project)
                      ? () => controller.startProject(project.id)
                      : null,
                  isStarting: controller.isStarting(project.id),
                ),
              );
            },
          ),
        ),
      ],
    );
  }
}

class _FilteredEmptyState extends StatelessWidget {
  const _FilteredEmptyState({required this.onClear});

  final VoidCallback onClear;

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
              Icons.filter_alt_off_outlined,
              size: 36,
              color: theme.colorScheme.outline,
            ),
            const SizedBox(height: AppSpacing.md),
            Text(
              'projects.noMatches.title'.tr(),
              style: theme.textTheme.titleMedium,
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.sm),
            Text(
              'projects.noMatches.message'.tr(),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: AppSpacing.md),
            TextButton(
              onPressed: onClear,
              child: Text('projects.clearFilter'.tr()),
            ),
          ],
        ),
      ),
    );
  }
}

/// Says out loud that the list is unpaged, which is the honest form of gap
/// `G-PROJECT-PAGING`: the client renders what the endpoint returns and does
/// not pretend to have page controls the API cannot serve.
class _UnpagedListNotice extends StatelessWidget {
  const _UnpagedListNotice({required this.total});

  final int total;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(AppSpacing.sm),
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(AppRadius.sm),
      ),
      child: UnavailableNote(
        title: 'projects.unpaged.title'.tr(),
        reason: 'projects.unpaged.message'.tr(args: ['$total']),
      ),
    );
  }
}
