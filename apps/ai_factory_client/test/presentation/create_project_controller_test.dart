import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/controllers/create_project_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late FakeProjectRepository projects;

  setUp(() => projects = FakeProjectRepository());

  CreateProjectController build() =>
      CreateProjectController(projects: projects);

  group('validation mirrors the backend', () {
    late CreateProjectController controller;

    setUp(() => controller = build());
    tearDown(() => controller.onClose());

    test('a name is required', () {
      expect(controller.validateName(''), 'validation.nameRequired');
      expect(controller.validateName('   '), 'validation.nameRequired');
      expect(controller.validateName('Customer API'), isNull);
    });

    test('a name over 200 characters is rejected', () {
      expect(controller.validateName('a' * 200), isNull);
      expect(controller.validateName('a' * 201), 'validation.nameTooLong');
    });

    test('path traversal and invalid filename characters are rejected', () {
      // The name becomes a workspace folder name, which is why the backend
      // validates it against Path.GetInvalidFileNameChars().
      for (final name in <String>[
        '../escape',
        'a..b',
        'with/slash',
        r'with\backslash',
        'with:colon',
        'with*star',
        'with?question',
        'with|pipe',
        'with"quote',
        'with<angle',
      ]) {
        expect(
          controller.validateName(name),
          'validation.nameInvalidCharacters',
          reason: 'should reject: $name',
        );
      }
    });

    test('a description is required, because Start rejects a project without one', () {
      expect(
        controller.validateDescription(''),
        'validation.descriptionRequired',
      );
      expect(controller.validateDescription('Build an API.'), isNull);
      expect(
        controller.validateDescription('a' * 2001),
        'validation.descriptionTooLong',
      );
    });

    test('a repository URL is required only for a Git source', () {
      expect(controller.validateRepository(''), isNull);

      controller.setSourceType(ProjectSourceType.gitRepository);
      expect(controller.validateRepository(''), 'validation.repositoryRequired');
      expect(
        controller.validateRepository('https://example.com/repo.git'),
        isNull,
      );
    });
  });

  group('create step', () {
    test('an invalid form sends no request', () async {
      final controller = build();
      controller.nameField.text = '';
      controller.descriptionField.text = '';

      expect(await controller.submitDetails(), isFalse);
      expect(projects.createRequests, isEmpty);
      expect(controller.step.value, CreateProjectStep.details);
      controller.onClose();
    });

    test('a valid form creates the project and advances', () async {
      final controller = build();
      controller.nameField.text = '  Customer API  ';
      controller.descriptionField.text = '  Build an API.  ';

      expect(await controller.submitDetails(), isTrue);

      expect(projects.createRequests, hasLength(1));
      // Trimmed before sending: trailing whitespace would become part of the
      // workspace folder name.
      expect(projects.createRequests.single.name, 'Customer API');
      expect(projects.createRequests.single.description, 'Build an API.');
      expect(controller.step.value, CreateProjectStep.source);
      expect(controller.project.value, isNotNull);
      controller.onClose();
    });

    test('a second submit while the first is in flight sends nothing', () async {
      final gate = Completer<void>();
      projects.gate = gate.future;

      final controller = build();
      controller.nameField.text = 'Customer API';
      controller.descriptionField.text = 'Build an API.';

      final first = controller.submitDetails();
      expect(controller.isSubmitting.value, isTrue);

      // The whole point: one press, one project.
      expect(await controller.submitDetails(), isFalse);
      expect(projects.createRequests, hasLength(1));

      gate.complete();
      await first;
      expect(controller.isSubmitting.value, isFalse);
      controller.onClose();
    });

    test('a server validation failure keeps the typed values', () async {
      projects.mutationError = const ValidationError(
        technicalDetail: 'Project name contains invalid characters.',
      );

      final controller = build();
      controller.nameField.text = 'Customer API';
      controller.descriptionField.text = 'Build an API.';

      expect(await controller.submitDetails(), isFalse);

      expect(controller.error.value, isA<ValidationError>());
      expect(
        controller.serverDetail.value,
        'Project name contains invalid characters.',
      );
      expect(controller.nameField.text, 'Customer API');
      expect(controller.descriptionField.text, 'Build an API.');
      expect(controller.step.value, CreateProjectStep.details);
      controller.onClose();
    });

    test('a network failure leaves the form retryable', () async {
      projects.mutationError = const NetworkError();
      projects.failOnce = true;

      final controller = build();
      controller.nameField.text = 'Customer API';
      controller.descriptionField.text = 'Build an API.';

      expect(await controller.submitDetails(), isFalse);
      expect(controller.error.value, isA<NetworkError>());

      expect(await controller.submitDetails(), isTrue);
      expect(controller.error.value, isNull);
      expect(projects.createRequests, hasLength(2));
      controller.onClose();
    });

    test('retrying after the project was created does not create a second', () async {
      final controller = build();
      controller.nameField.text = 'Customer API';
      controller.descriptionField.text = 'Build an API.';
      await controller.submitDetails();

      controller.step.value = CreateProjectStep.details;
      expect(await controller.submitDetails(), isTrue);

      expect(projects.createRequests, hasLength(1));
      expect(controller.step.value, CreateProjectStep.source);
      controller.onClose();
    });
  });

  group('source step', () {
    Future<CreateProjectController> created() async {
      final controller = build();
      controller.nameField.text = 'Customer API';
      controller.descriptionField.text = 'Build an API.';
      await controller.submitDetails();
      return controller;
    }

    test('an empty source completes the flow', () async {
      final controller = await created();

      expect(await controller.submitSource(), isTrue);

      expect(controller.step.value, CreateProjectStep.done);
      expect(projects.calls, contains(startsWith('initializeSource:')));
      controller.onClose();
    });

    test('a Git source requires a repository URL', () async {
      final controller = await created();
      controller.setSourceType(ProjectSourceType.gitRepository);

      expect(await controller.submitSource(), isFalse);
      expect(
        projects.calls.where((c) => c.startsWith('initializeSource')),
        isEmpty,
      );

      controller.repositoryField.text = 'https://example.com/repo.git';
      expect(await controller.submitSource(), isTrue);
      controller.onClose();
    });

    test('a 409 means already initialized, which is the desired outcome', () async {
      final controller = await created();
      projects.mutationError = const ConflictError(
        technicalDetail: 'Source already initialized.',
      );
      projects.failOnce = true;

      expect(await controller.submitSource(), isTrue);

      expect(controller.step.value, CreateProjectStep.done);
      expect(controller.error.value, isNull);
      // It re-read the project rather than assuming what state it is in.
      expect(projects.calls, contains(startsWith('getById:')));
      controller.onClose();
    });

    test('a failing source step keeps the created project for a retry', () async {
      final controller = await created();
      final project = controller.project.value;

      projects.mutationError = const ProviderFailure();
      projects.failOnce = true;
      expect(await controller.submitSource(), isFalse);

      expect(controller.error.value, isA<ProviderFailure>());
      expect(controller.step.value, CreateProjectStep.source);
      // The project row exists on the server; a retry must not create a
      // second one.
      expect(controller.project.value, project);

      expect(await controller.submitSource(), isTrue);
      expect(projects.createRequests, hasLength(1));
      controller.onClose();
    });

    test('a second submit while the first is in flight sends nothing', () async {
      final controller = await created();
      final gate = Completer<void>();
      projects.gate = gate.future;

      final first = controller.submitSource();
      expect(await controller.submitSource(), isFalse);

      gate.complete();
      await first;

      expect(
        projects.calls.where((c) => c.startsWith('initializeSource')),
        hasLength(1),
      );
      controller.onClose();
    });
  });

  test('resuming a stuck project starts at the source step', () async {
    // A project created in an earlier attempt whose source step failed is not
    // a dead end: the flow reopens where it stopped.
    final stuck = testProject(id: 'p1', status: ProjectStatus.created);
    final controller = CreateProjectController(
      projects: projects,
      resumeFrom: stuck,
    );

    expect(controller.step.value, CreateProjectStep.source);
    expect(controller.project.value, stuck);

    expect(await controller.submitSource(), isTrue);
    expect(projects.createRequests, isEmpty);
    controller.onClose();
  });
}
