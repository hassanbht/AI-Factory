import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:ai_factory_client/presentation/models/project_lifecycle.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

/// `ProjectDetailController` against a scripted repository.
///
/// Deterministic on purpose: no HTTP, no provider, no clock. What is being
/// pinned down is the controller's own contract — which state it publishes,
/// how many requests one gesture produces, and that it never writes a project
/// status the server did not return.
void main() {
  const projectId = 'p1';

  late FakeProjectRepository repository;

  ProjectDetailController controllerFor(FakeProjectRepository repo) =>
      ProjectDetailController(projectId: projectId, projects: repo);

  /// `onInit` is what the route binding calls; a unit test has to call it.
  ProjectDetailController started(FakeProjectRepository repo) =>
      controllerFor(repo)..onInit();

  setUp(() {
    repository = FakeProjectRepository(
      projects: <Project>[testProject(id: projectId)],
    );
  });

  group('load', () {
    test('publishes Loading before the first response arrives', () async {
      final gate = Completer<void>();
      repository.gate = gate.future;

      final controller = started(repository);
      expect(controller.state.value, isA<Loading<Project>>());
      expect(controller.project, isNull);

      gate.complete();
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Success<Project>>());
    });

    test('reads the project by its id and publishes it', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      expect(repository.calls, contains('getById:$projectId'));
      expect(controller.project?.id, projectId);
      expect(controller.state.value, isA<Success<Project>>());
    });

    test('404 is a not-found state, not a server failure', () async {
      repository.getByIdError = const NotFoundError();

      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      expect(controller.isNotFound, isTrue);
      expect(controller.project, isNull);
    });

    test('a server error is a failure with a retry, not a not-found', () async {
      repository.getByIdError = const ServerError();

      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final state = controller.state.value;
      expect(state, isA<Failure<Project>>());
      expect((state as Failure<Project>).error, isA<ServerError>());
      expect(controller.isNotFound, isFalse);
    });

    test('a network error is reported as such', () async {
      repository.getByIdError = const NetworkError();

      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final state = controller.state.value;
      expect(state, isA<Failure<Project>>());
      expect((state as Failure<Project>).error, isA<NetworkError>());
    });

    test('retrying a failed load recovers', () async {
      repository
        ..getByIdError = const NetworkError()
        ..failOnce = true;

      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);
      expect(controller.state.value, isA<Failure<Project>>());

      await controller.load();
      expect(controller.state.value, isA<Success<Project>>());
      expect(repository.getByIdCount, 2);
    });
  });

  group('refresh', () {
    test('keeps the project on screen while the read is in flight', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final gate = Completer<void>();
      repository.gate = gate.future;
      final pending = controller.refreshData();

      expect(controller.state.value, isA<Refreshing<Project>>());
      // The point of Refreshing: the data is still there.
      expect(controller.project?.id, projectId);

      gate.complete();
      await pending;
      expect(controller.state.value, isA<Success<Project>>());
    });

    test('publishes the status the server returned, not a guess', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);
      expect(controller.project?.status, ProjectStatus.analyzing);

      // The Worker moved the project on between the two reads.
      repository.getByIdResult = testProject(
        id: projectId,
        status: ProjectStatus.implementation,
        lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
      );
      await controller.refreshData();

      expect(controller.project?.status, ProjectStatus.implementation);
      expect(
        controller.lifecycle?.currentNode?.status,
        ProjectStatus.implementation,
      );
    });

    test('a failed refresh keeps the last known project as stale', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      repository.getByIdError = const NetworkError();
      await controller.refreshData();

      expect(controller.state.value, isA<Failure<Project>>());
      // Stale beats blank: the operator keeps the project they were reading.
      expect(controller.project?.id, projectId);
    });

    test('a 404 on refresh drops the stale copy', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      repository.getByIdError = const NotFoundError();
      await controller.refreshData();

      expect(controller.isNotFound, isTrue);
      expect(controller.project, isNull);
    });

    test('a second refresh supersedes the first', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final gate = Completer<void>();
      repository.gate = gate.future;

      final first = controller.refreshData();
      final second = controller.refreshData();
      gate.complete();
      await Future.wait(<Future<void>>[first, second]);

      // Both requests were issued; only the newest owns the state.
      expect(controller.state.value, isA<Success<Project>>());
    });
  });

  group('start', () {
    test('is offered only in the state the backend accepts', () async {
      Future<bool> canStartWith(Project project) async {
        final repo = FakeProjectRepository(projects: <Project>[project]);
        final controller = started(repo);
        await Future<void>.delayed(Duration.zero);
        return controller.canStart;
      }

      expect(
        await canStartWith(
          testProject(id: projectId, status: ProjectStatus.analyzing),
        ),
        isTrue,
      );
      // Created has no initialized source; the backend answers 409.
      expect(
        await canStartWith(
          testProject(id: projectId, status: ProjectStatus.created),
        ),
        isFalse,
      );
      // Already started, whatever stage it has since reached.
      expect(
        await canStartWith(
          testProject(
            id: projectId,
            status: ProjectStatus.analyzing,
            lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
          ),
        ),
        isFalse,
      );
      expect(
        await canStartWith(
          testProject(id: projectId, status: ProjectStatus.completed),
        ),
        isFalse,
      );
      expect(
        await canStartWith(
          testProject(id: projectId, status: ProjectStatus.failed),
        ),
        isFalse,
      );
    });

    test('publishes the project the server returned', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      repository.startResult = testProject(
        id: projectId,
        status: ProjectStatus.planning,
        lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
      );
      await controller.startProject(actor: 'operator');

      expect(repository.startedIds, <String>[projectId]);
      expect(controller.project?.status, ProjectStatus.planning);
      expect(controller.project?.isStarted, isTrue);
      expect(controller.canStart, isFalse);
      expect(controller.actionError.value, isNull);
    });

    test('a second press while one is in flight is dropped', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final gate = Completer<void>();
      repository.gate = gate.future;

      final first = controller.startProject();
      final second = controller.startProject();
      expect(controller.isStarting.value, isTrue);

      gate.complete();
      await Future.wait(<Future<void>>[first, second]);

      expect(repository.startCount, 1, reason: 'one gesture, one request');
      expect(controller.isStarting.value, isFalse);
    });

    test('a rejection is an action error, not a blanked screen', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      repository
        ..mutationError = const ConflictError()
        ..failOnce = true;
      await controller.startProject();

      expect(controller.actionError.value, isA<ConflictError>());
      // The project is still on screen...
      expect(controller.project, isNotNull);
      // ...and was re-read, because a 409 means the client's copy is stale.
      expect(repository.getByIdCount, 2);

      controller.dismissActionError();
      expect(controller.actionError.value, isNull);
    });

    test('a 404 on start re-reads and lands on the not-found state', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      repository
        ..mutationError = const NotFoundError()
        ..failOnce = true
        ..getByIdError = const NotFoundError();
      await controller.startProject();

      expect(controller.actionError.value, isA<NotFoundError>());
      expect(controller.isNotFound, isTrue);
    });

    test('no status is written locally before the server answers', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final gate = Completer<void>();
      repository.gate = gate.future;
      final pending = controller.startProject();

      // Mid-flight: the status is still the one the last read returned.
      expect(controller.project?.status, ProjectStatus.analyzing);
      expect(controller.project?.isStarted, isFalse);

      gate.complete();
      await pending;
    });
  });

  group('disposal', () {
    test('an in-flight read is cancelled and never lands', () async {
      final gate = Completer<void>();
      repository.gate = gate.future;

      final controller = started(repository);
      expect(controller.state.value, isA<Loading<Project>>());

      controller.onClose();
      gate.complete();
      await Future<void>.delayed(Duration.zero);

      // The response is discarded rather than published into a disposed
      // controller.
      expect(controller.state.value, isA<Loading<Project>>());
    });

    test('closing twice is safe', () async {
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      controller.onClose();
      expect(controller.onClose, returnsNormally);
    });
  });

  group('lifecycle projection', () {
    test('is null until something has loaded', () {
      final controller = controllerFor(repository);
      expect(controller.lifecycle, isNull);
    });

    test('reflects the reported status and claims no history', () async {
      repository.projects = <Project>[
        testProject(id: projectId, status: ProjectStatus.reviewing),
      ];
      final controller = started(repository);
      await Future<void>.delayed(Duration.zero);

      final lifecycle = controller.lifecycle!;
      expect(lifecycle.currentNode?.status, ProjectStatus.reviewing);
      expect(
        lifecycle.nodes
            .where((n) => n.position == LifecyclePosition.current)
            .length,
        1,
      );
    });
  });
}
