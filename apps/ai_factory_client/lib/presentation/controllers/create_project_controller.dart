import 'package:flutter/widgets.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../data/dto/requests.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';

/// Where the operator is in the create flow.
enum CreateProjectStep {
  /// Name and description -> `POST /api/projects`.
  details,

  /// Source choice -> `POST /api/projects/{id}/source`.
  source,

  /// Both calls succeeded; the project is `Analyzing` and can be started.
  done,
}

/// Create Project.
///
/// Two backend operations, in the only order the API accepts:
///
///     POST /api/projects              -> Created
///     POST /api/projects/{id}/source  -> Analyzing
///
/// They are separate steps because they fail separately — a Git clone can
/// fail long after the project row exists. A failure therefore never discards
/// the step that already succeeded, and never discards what the operator
/// typed: the same [project] is reused when the source step is retried, so a
/// retry cannot create a second project.
///
/// Starting is deliberately **not** part of this flow. It is a distinct
/// lifecycle decision the operator takes from the Projects screen, against a
/// project that already exists.
class CreateProjectController extends GetxController {
  CreateProjectController({ProjectRepository? projects, Project? resumeFrom})
    : _projects = projects ?? Get.find<ProjectRepository>(),
      project = Rxn<Project>(resumeFrom),
      step = Rx<CreateProjectStep>(
        resumeFrom == null
            ? CreateProjectStep.details
            : CreateProjectStep.source,
      );

  /// Mirrors `Path.GetInvalidFileNameChars()` on Windows, which is what the
  /// backend validates against. Checked client-side so the operator is told
  /// before the round trip — the server still decides.
  static const String invalidNameCharacters = r'<>:"/\|?*';

  static const int maxNameLength = 200;
  static const int maxDescriptionLength = 2000;

  final ProjectRepository _projects;

  final TextEditingController nameField = TextEditingController();
  final TextEditingController descriptionField = TextEditingController();

  final Rx<CreateProjectStep> step;

  /// Set once `POST /api/projects` has succeeded. Its presence is what makes
  /// a retry of the source step safe.
  final Rxn<Project> project;

  final Rx<ProjectSourceType> sourceType = Rx<ProjectSourceType>(
    ProjectSourceType.empty,
  );
  final TextEditingController repositoryField = TextEditingController();
  final TextEditingController branchField = TextEditingController();

  /// True while a request is in flight. Every submit button reads it, which
  /// is what makes a double-click a no-op.
  final RxBool isSubmitting = false.obs;

  final Rxn<AppError> error = Rxn<AppError>();

  /// Server-side validation text, shown beside the localized message because
  /// it names the field the backend rejected.
  final RxnString serverDetail = RxnString();

  RequestCancellation? _inFlight;

  @override
  void onClose() {
    _inFlight?.cancel();
    nameField.dispose();
    descriptionField.dispose();
    repositoryField.dispose();
    branchField.dispose();
    super.onClose();
  }

  // --- Validation --------------------------------------------------------
  //
  // Mirrors the backend exactly (`POST /api/projects` in `Program.cs`), plus
  // one rule the create endpoint does not enforce but `start` does: a project
  // without a description can never be started, so it is required here rather
  // than producing a project that is dead on arrival.

  /// Translation key of the violated rule, or null when valid.
  String? validateName(String? value) {
    final name = value?.trim() ?? '';
    if (name.isEmpty) return 'validation.nameRequired';
    if (name.length > maxNameLength) return 'validation.nameTooLong';
    if (name.contains('..')) return 'validation.nameInvalidCharacters';
    if (invalidNameCharacters.split('').any(name.contains)) {
      return 'validation.nameInvalidCharacters';
    }
    if (name.codeUnits.any((unit) => unit < 32)) {
      return 'validation.nameInvalidCharacters';
    }
    return null;
  }

  String? validateDescription(String? value) {
    final description = value?.trim() ?? '';
    if (description.isEmpty) return 'validation.descriptionRequired';
    if (description.length > maxDescriptionLength) {
      return 'validation.descriptionTooLong';
    }
    return null;
  }

  String? validateRepository(String? value) {
    if (sourceType.value != ProjectSourceType.gitRepository) return null;
    final location = value?.trim() ?? '';
    if (location.isEmpty) return 'validation.repositoryRequired';
    return null;
  }

  bool get isDetailsValid =>
      validateName(nameField.text) == null &&
      validateDescription(descriptionField.text) == null;

  bool get isSourceValid => validateRepository(repositoryField.text) == null;

  void setSourceType(ProjectSourceType type) {
    sourceType.value = type;
    error.value = null;
    serverDetail.value = null;
  }

  // --- Steps -------------------------------------------------------------

  /// `POST /api/projects`. On success the flow advances to the source step
  /// and the created project is remembered, so pressing Create again cannot
  /// produce a duplicate.
  Future<bool> submitDetails() async {
    if (isSubmitting.value) return false;
    if (project.value != null) {
      // Already created in an earlier attempt; nothing to repeat.
      step.value = CreateProjectStep.source;
      return true;
    }
    if (!isDetailsValid) return false;

    return _run(() async {
      final created = await _projects.create(
        CreateProjectRequest(
          name: nameField.text.trim(),
          description: descriptionField.text.trim(),
        ),
        cancellation: _inFlight,
      );
      project.value = created;
      step.value = CreateProjectStep.source;
      return true;
    });
  }

  /// `POST /api/projects/{id}/source`.
  ///
  /// A `409` here means the source is already initialized — which is the
  /// outcome this step was trying to reach. Treating it as success is what
  /// makes a retry after an ambiguous timeout safe; the project the
  /// **server** holds is re-read so the screen shows its real status.
  Future<bool> submitSource() async {
    if (isSubmitting.value) return false;
    final created = project.value;
    if (created == null) return false;
    if (!isSourceValid) return false;

    return _run(() async {
      try {
        final updated = await _projects.initializeSource(
          created.id,
          _sourceRequest(),
          cancellation: _inFlight,
        );
        project.value = updated;
      } on ConflictError {
        project.value = await _projects.getById(
          created.id,
          cancellation: _inFlight,
        );
      }
      step.value = CreateProjectStep.done;
      return true;
    });
  }

  InitializeProjectSourceRequest _sourceRequest() {
    if (sourceType.value != ProjectSourceType.gitRepository) {
      return const InitializeProjectSourceRequest.empty();
    }
    final branch = branchField.text.trim();
    return InitializeProjectSourceRequest.git(
      repositoryUrl: repositoryField.text.trim(),
      branch: branch.isEmpty ? null : branch,
    );
  }

  /// Runs one step with the submit guard, cancellation and error capture that
  /// every step needs identically.
  Future<bool> _run(Future<bool> Function() action) async {
    isSubmitting.value = true;
    error.value = null;
    serverDetail.value = null;
    _inFlight = RequestCancellation();

    try {
      return await action();
    } on RequestCancelledError {
      return false;
    } on AppError catch (failure) {
      error.value = failure;
      serverDetail.value = failure is ValidationError
          ? failure.technicalDetail
          : null;
      return false;
    } finally {
      _inFlight = null;
      isSubmitting.value = false;
    }
  }
}
