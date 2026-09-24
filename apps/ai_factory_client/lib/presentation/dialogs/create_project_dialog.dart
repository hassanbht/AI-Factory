import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_motion.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../controllers/create_project_controller.dart';
import '../widgets/panels.dart';
import '../widgets/status_chip.dart';

/// Opens the create flow and returns the created project, or null if the
/// operator closed it without creating one.
///
/// A project that was created but whose source step failed is still returned:
/// it exists on the server, so the list must show it rather than pretend the
/// operator's work was discarded.
Future<Project?> showCreateProjectDialog(
  BuildContext context, {
  Project? resumeFrom,
  CreateProjectController? controller,
}) {
  final flow = controller ?? CreateProjectController(resumeFrom: resumeFrom);

  return showDialog<Project>(
    context: context,
    barrierDismissible: false,
    animationStyle: AppMotion.dialogAnimation(context),
    // Only what this function created is disposed; a caller-supplied
    // controller (a test, or a resumed flow) owns its own lifetime.
    builder: (context) => CreateProjectDialog(
      controller: flow,
      ownsController: controller == null,
    ),
  );
}

/// Create Project.
///
/// Two steps because the backend has two operations, and they fail
/// independently:
///
///     details -> POST /api/projects
///     source  -> POST /api/projects/{id}/source
///
/// Neither step discards what the operator typed, and neither repeats a call
/// that already succeeded. Starting the project is a separate decision taken
/// from the Projects list — this dialog never starts anything.
///
/// The body and the actions are built by functions rather than by child
/// widgets, so every observable is read while the `Obx` closure is running.
/// A child widget's `build` executes outside that scope, and GetX would not
/// track what it reads.
class CreateProjectDialog extends StatefulWidget {
  const CreateProjectDialog({
    required this.controller,
    this.ownsController = false,
    super.key,
  });

  final CreateProjectController controller;

  /// Dispose [controller] with the dialog. The dialog's widgets outlive the
  /// `showDialog` future — they are still built while the exit transition
  /// runs — so disposing the form fields when the future completes used
  /// them after disposal. The State's own `dispose` runs after the last
  /// frame that shows them.
  final bool ownsController;

  @override
  State<CreateProjectDialog> createState() => _CreateProjectDialogState();
}

class _CreateProjectDialogState extends State<CreateProjectDialog> {
  /// One key per step, because each step is its own form with its own rules.
  /// Submitting runs `validate()` explicitly, so pressing the button on an
  /// untouched form states what is missing instead of doing nothing.
  final GlobalKey<FormState> _detailsForm = GlobalKey<FormState>();
  final GlobalKey<FormState> _sourceForm = GlobalKey<FormState>();

  CreateProjectController get controller => widget.controller;

  @override
  void dispose() {
    if (widget.ownsController) controller.onClose();
    super.dispose();
  }

  Future<void> _submit() async {
    final key = switch (controller.step.value) {
      CreateProjectStep.details => _detailsForm,
      CreateProjectStep.source => _sourceForm,
      CreateProjectStep.done => null,
    };
    if (key?.currentState?.validate() == false) return;

    switch (controller.step.value) {
      case CreateProjectStep.details:
        await controller.submitDetails();
      case CreateProjectStep.source:
        await controller.submitSource();
      case CreateProjectStep.done:
        break;
    }
  }

  @override
  Widget build(BuildContext context) {
    return ResponsiveBuilder(
      builder: (context, sizeClass) {
        final dialogWidth = sizeClass.isCompact ? double.infinity : 560.0;

        // Escape closes the dialog exactly as Cancel does — and, like
        // Cancel, not while a request is in flight. The barrier stays
        // non-dismissible, so a stray click outside never loses the form.
        return Actions(
          actions: <Type, Action<Intent>>{
            DismissIntent: CallbackAction<DismissIntent>(
              onInvoke: (_) {
                if (!controller.isSubmitting.value) {
                  Navigator.of(context).pop(controller.project.value);
                }
                return null;
              },
            ),
          },
          child: AlertDialog(
            title: Text('projects.create.title'.tr()),
            content: SizedBox(
              width: dialogWidth,
              child: SingleChildScrollView(
                child: Obx(
                  () => _buildContent(
                    context,
                    controller,
                    detailsForm: _detailsForm,
                    sourceForm: _sourceForm,
                  ),
                ),
              ),
            ),
            actions: [
              Obx(() => _buildActions(context, controller, onSubmit: _submit)),
            ],
          ),
        );
      },
    );
  }
}

Widget _buildContent(
  BuildContext context,
  CreateProjectController controller, {
  required GlobalKey<FormState> detailsForm,
  required GlobalKey<FormState> sourceForm,
}) {
  final step = controller.step.value;
  final error = controller.error.value;
  final detail = controller.serverDetail.value;

  return Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: [
      _StepIndicator(step: step),
      const SizedBox(height: AppSpacing.md),
      if (error != null) ...[
        ActionErrorBanner(error: error),
        if (detail != null) ...[
          const SizedBox(height: AppSpacing.xs),
          Text(detail, style: Theme.of(context).textTheme.bodySmall),
        ],
        const SizedBox(height: AppSpacing.md),
      ],
      switch (step) {
        CreateProjectStep.details => Form(
          key: detailsForm,
          child: _buildDetailsStep(context, controller),
        ),
        CreateProjectStep.source => Form(
          key: sourceForm,
          child: _buildSourceStep(context, controller),
        ),
        CreateProjectStep.done => _buildDoneStep(context, controller),
      },
    ],
  );
}

class _StepIndicator extends StatelessWidget {
  const _StepIndicator({required this.step});

  final CreateProjectStep step;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final index = step.index;

    return Semantics(
      label: 'projects.create.stepOf'.tr(
        args: ['${index + 1}', '${CreateProjectStep.values.length}'],
      ),
      child: Row(
        children: [
          for (var i = 0; i < CreateProjectStep.values.length; i++) ...[
            if (i > 0) const SizedBox(width: AppSpacing.xs),
            Expanded(
              child: Container(
                height: 4,
                decoration: BoxDecoration(
                  color: i <= index
                      ? theme.colorScheme.primary
                      : theme.colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

Widget _buildDetailsStep(
  BuildContext context,
  CreateProjectController controller,
) {
  final busy = controller.isSubmitting.value;

  return Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: [
      TextFormField(
        controller: controller.nameField,
        autofocus: true,
        enabled: !busy,
        maxLength: CreateProjectController.maxNameLength,
        decoration: InputDecoration(
          labelText: 'projects.create.name'.tr(),
          helperText: 'projects.create.nameHelp'.tr(),
          border: const OutlineInputBorder(),
        ),
        // The rule the validator returns is a translation key, resolved here:
        // the controller stays free of presentation concerns.
        validator: (value) => controller.validateName(value)?.tr(),
        autovalidateMode: AutovalidateMode.onUserInteraction,
      ),
      const SizedBox(height: AppSpacing.sm),
      TextFormField(
        controller: controller.descriptionField,
        enabled: !busy,
        minLines: 3,
        maxLines: 6,
        maxLength: CreateProjectController.maxDescriptionLength,
        decoration: InputDecoration(
          labelText: 'projects.create.description'.tr(),
          // Says *why* it is required, which is a backend rule the operator
          // would otherwise discover only when Start fails.
          helperText: 'projects.create.descriptionHelp'.tr(),
          border: const OutlineInputBorder(),
        ),
        validator: (value) => controller.validateDescription(value)?.tr(),
        autovalidateMode: AutovalidateMode.onUserInteraction,
      ),
    ],
  );
}

Widget _buildSourceStep(
  BuildContext context,
  CreateProjectController controller,
) {
  final theme = Theme.of(context);
  final sourceType = controller.sourceType.value;
  final project = controller.project.value;
  final busy = controller.isSubmitting.value;
  final isGit = sourceType == ProjectSourceType.gitRepository;

  return Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: [
      if (project != null) ...[
        Text(
          'projects.create.createdAs'.tr(args: [project.name]),
          style: theme.textTheme.bodyMedium,
        ),
        const SizedBox(height: AppSpacing.md),
      ],
      Text('projects.create.source'.tr(), style: theme.textTheme.titleSmall),
      const SizedBox(height: AppSpacing.xs),
      Text(
        'projects.create.sourceHelp'.tr(),
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
      const SizedBox(height: AppSpacing.sm),
      RadioGroup<ProjectSourceType>(
        groupValue: sourceType,
        onChanged: (value) {
          // Ignored while a request is in flight: the source choice must not
          // change under a call that is already using it.
          if (busy || value == null) return;
          controller.setSourceType(value);
        },
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            RadioListTile<ProjectSourceType>(
              value: ProjectSourceType.empty,
              title: Text('projects.create.sourceEmpty'.tr()),
              subtitle: Text('projects.create.sourceEmptyHelp'.tr()),
              contentPadding: EdgeInsets.zero,
            ),
            RadioListTile<ProjectSourceType>(
              value: ProjectSourceType.gitRepository,
              title: Text('projects.create.sourceGit'.tr()),
              subtitle: Text('projects.create.sourceGitHelp'.tr()),
              contentPadding: EdgeInsets.zero,
            ),
          ],
        ),
      ),
      if (isGit) ...[
        const SizedBox(height: AppSpacing.sm),
        TextFormField(
          controller: controller.repositoryField,
          enabled: !busy,
          decoration: InputDecoration(
            labelText: 'projects.create.repository'.tr(),
            border: const OutlineInputBorder(),
          ),
          validator: (value) => controller.validateRepository(value)?.tr(),
          autovalidateMode: AutovalidateMode.onUserInteraction,
        ),
        const SizedBox(height: AppSpacing.sm),
        TextFormField(
          controller: controller.branchField,
          enabled: !busy,
          decoration: InputDecoration(
            labelText: 'projects.create.branch'.tr(),
            helperText: 'projects.create.branchHelp'.tr(),
            border: const OutlineInputBorder(),
          ),
        ),
      ],
    ],
  );
}

Widget _buildDoneStep(
  BuildContext context,
  CreateProjectController controller,
) {
  final theme = Theme.of(context);
  final project = controller.project.value;

  return Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    mainAxisSize: MainAxisSize.min,
    children: [
      Row(
        children: [
          Icon(Icons.check_circle_outline, color: theme.colorScheme.primary),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Text(
              'projects.create.success'.tr(),
              style: theme.textTheme.titleSmall,
            ),
          ),
        ],
      ),
      if (project != null) ...[
        const SizedBox(height: AppSpacing.md),
        Text(project.name, style: theme.textTheme.bodyLarge),
        const SizedBox(height: AppSpacing.xs),
        // The status shown is the one the server returned, never one assumed
        // from having called the endpoint.
        StatusChip.project(project.status, rawStatus: project.rawStatus),
      ],
      const SizedBox(height: AppSpacing.md),
      Text(
        'projects.create.startHint'.tr(),
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
    ],
  );
}

Widget _buildActions(
  BuildContext context,
  CreateProjectController controller, {
  required Future<void> Function() onSubmit,
}) {
  final busy = controller.isSubmitting.value;
  final step = controller.step.value;

  void close() => Navigator.of(context).pop(controller.project.value);

  // An OverflowBar, not a Row: at 200 % text in German the two buttons do
  // not fit side by side on a phone-width dialog, and they stack instead of
  // being clipped.
  return OverflowBar(
    alignment: MainAxisAlignment.end,
    overflowAlignment: OverflowBarAlignment.end,
    spacing: AppSpacing.sm,
    overflowSpacing: AppSpacing.sm,
    children: [
      if (step != CreateProjectStep.done)
        TextButton(
          // Cancelling after the project was created still returns it: the
          // row exists on the server either way.
          onPressed: busy ? null : close,
          child: Text('common.cancel'.tr()),
        ),
      switch (step) {
        CreateProjectStep.details => FilledButton(
          // Disabled while the request is in flight, which is what stops a
          // double-click from creating two projects.
          onPressed: busy ? null : onSubmit,
          child: busy
              ? const _ButtonSpinner()
              : Text('projects.create.submit'.tr()),
        ),
        CreateProjectStep.source => FilledButton(
          onPressed: busy ? null : onSubmit,
          child: busy
              ? const _ButtonSpinner()
              : Text('projects.create.initializeSource'.tr()),
        ),
        CreateProjectStep.done => FilledButton(
          onPressed: close,
          child: Text('common.close'.tr()),
        ),
      },
    ],
  );
}

class _ButtonSpinner extends StatelessWidget {
  const _ButtonSpinner();

  @override
  Widget build(BuildContext context) {
    return const SizedBox(
      width: 16,
      height: 16,
      child: CircularProgressIndicator(strokeWidth: 2),
    );
  }
}
