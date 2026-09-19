import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/presentation/controllers/projects_controller.dart';
import 'package:ai_factory_client/presentation/models/project_list_filter.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

void main() {
  late FakeProjectRepository projects;

  setUp(() => projects = FakeProjectRepository());

  ProjectsController build() => ProjectsController(projects: projects);

  group('loading', () {
    test('an empty collection is Empty, not Success with no rows', () async {
      final controller = build();
      await controller.load();

      expect(controller.state.value, isA<Empty<List<Project>>>());
      expect(controller.visibleProjects, isEmpty);
    });

    test('a populated collection is Success', () async {
      projects.projects = [testProject(), testProject(id: 'p2', name: 'Two')];

      final controller = build();
      await controller.load();

      expect(controller.state.value, isA<Success<List<Project>>>());
      expect(controller.visibleProjects, hasLength(2));
      expect(controller.totalCount, 2);
    });

    test('a transport failure is surfaced with a retry path', () async {
      projects.listError = const NetworkError();

      final controller = build();
      await controller.load();
      expect(controller.state.value, isA<Failure<List<Project>>>());

      projects.listError = null;
      projects.projects = [testProject()];
      await controller.load();
      expect(controller.state.value, isA<Success<List<Project>>>());
    });
  });

  group('sorting', () {
    test('groups in-progress first, then review, completed, failed', () async {
      projects.projects = [
        testProject(id: 'failed', status: ProjectStatus.failed),
        testProject(id: 'done', status: ProjectStatus.completed),
        testProject(id: 'review', status: ProjectStatus.humanReview),
        testProject(id: 'running', status: ProjectStatus.implementation),
      ];

      final controller = build();
      await controller.load();

      expect(
        controller.visibleProjects.map((p) => p.id).toList(),
        <String>['running', 'review', 'done', 'failed'],
      );
    });

    test('within a group, most recently updated comes first', () async {
      projects.projects = [
        testProject(
          id: 'old',
          status: ProjectStatus.implementation,
          updatedAtUtc: DateTime.utc(2026, 9, 18),
        ),
        testProject(
          id: 'new',
          status: ProjectStatus.implementation,
          updatedAtUtc: DateTime.utc(2026, 9, 19),
        ),
      ];

      final controller = build();
      await controller.load();

      expect(
        controller.visibleProjects.map((p) => p.id).toList(),
        <String>['new', 'old'],
      );
    });
  });

  group('filtering', () {
    setUp(() {
      projects.projects = [
        testProject(id: '1', name: 'Customer API', status: ProjectStatus.implementation),
        testProject(id: '2', name: 'Mobile App', status: ProjectStatus.humanReview),
        testProject(id: '3', name: 'Billing', status: ProjectStatus.completed),
        testProject(id: '4', name: 'Legacy', status: ProjectStatus.cancelled),
      ];
    });

    test('filters by status group', () async {
      final controller = build();
      await controller.load();

      controller.setStatusFilter(ProjectStatusFilter.humanReview);
      expect(controller.visibleProjects.map((p) => p.id), <String>['2']);

      // Cancelled belongs with failed: both mean "this will not finish".
      controller.setStatusFilter(ProjectStatusFilter.failed);
      expect(controller.visibleProjects.map((p) => p.id), <String>['4']);
    });

    test('filters by name, case-insensitively', () async {
      final controller = build();
      await controller.load();

      controller.setQuery('mobile');
      expect(controller.visibleProjects.map((p) => p.id), <String>['2']);
    });

    test('a filter that matches nothing empties the view, not the data', () async {
      final controller = build();
      await controller.load();

      controller.setQuery('nothing matches this');
      expect(controller.visibleProjects, isEmpty);
      // The screen distinguishes these two: there ARE projects.
      expect(controller.totalCount, 4);
      expect(controller.state.value, isA<Success<List<Project>>>());

      controller.clearFilter();
      expect(controller.visibleProjects, hasLength(4));
      expect(controller.isFiltered, isFalse);
    });
  });

  group('start', () {
    setUp(() {
      projects.projects = [
        testProject(id: 'p1', status: ProjectStatus.analyzing),
      ];
    });

    test('replaces the row with the project the server returned', () async {
      final controller = build();
      await controller.load();
      expect(controller.visibleProjects.single.isStarted, isFalse);

      await controller.startProject('p1');

      expect(projects.startedIds, <String>['p1']);
      // The visible state is the persisted one, never one assumed locally.
      expect(controller.visibleProjects.single.isStarted, isTrue);
      expect(controller.lastStarted.value, isNotNull);
      expect(controller.actionError.value, isNull);
    });

    test('a second press while the first is in flight sends nothing', () async {
      final controller = build();
      await controller.load();

      // The gate is installed only now, so the initial load is not blocked;
      // from here the Start call stays in flight until the test releases it.
      final gate = Completer<void>();
      projects.gate = gate.future;

      final first = controller.startProject('p1');
      expect(controller.isStarting('p1'), isTrue);

      // A double-click: the guard drops it rather than issuing a second
      // request, and `start` is idempotent server-side as well.
      await controller.startProject('p1');
      expect(projects.startedIds, hasLength(1));

      gate.complete();
      await first;
      expect(controller.isStarting('p1'), isFalse);
    });

    test('a conflict is shown and the list is re-read', () async {
      projects.mutationError = const ConflictError(
        technicalDetail: 'Project has no initialized source.',
      );

      final controller = build();
      await controller.load();
      final before = projects.listCount;

      await controller.startProject('p1');

      expect(controller.actionError.value, isA<ConflictError>());
      // The client's copy disagreed with the server, so it re-reads rather
      // than leaving a row the operator cannot act on.
      expect(projects.listCount, before + 1);
    });

    test('a validation failure is shown without re-reading', () async {
      projects.mutationError = const ValidationError(
        technicalDetail: 'Project has no description.',
      );

      final controller = build();
      await controller.load();
      final before = projects.listCount;

      await controller.startProject('p1');

      expect(controller.actionError.value, isA<ValidationError>());
      expect(projects.listCount, before);
      expect(controller.isStarting('p1'), isFalse);
    });

    test('a not-found project is shown and the list is re-read', () async {
      projects.mutationError = const NotFoundError();

      final controller = build();
      await controller.load();
      final before = projects.listCount;

      await controller.startProject('p1');

      expect(controller.actionError.value, isA<NotFoundError>());
      expect(projects.listCount, before + 1);
    });

    test('a timeout is shown and the row stays actionable', () async {
      projects.mutationError = const TimeoutError();

      final controller = build();
      await controller.load();
      await controller.startProject('p1');

      expect(controller.actionError.value, isA<TimeoutError>());
      expect(controller.isStarting('p1'), isFalse);

      // Retrying is safe: the endpoint is idempotent.
      projects.mutationError = null;
      await controller.startProject('p1');
      expect(controller.actionError.value, isNull);
      expect(controller.visibleProjects.single.isStarted, isTrue);
    });

    test('a server failure is shown', () async {
      projects.mutationError = const ServerError();

      final controller = build();
      await controller.load();
      await controller.startProject('p1');

      expect(controller.actionError.value, isA<ServerError>());
      expect(controller.lastStarted.value, isNull);
    });

    test('the error banner can be dismissed', () async {
      projects.mutationError = const ServerError();
      final controller = build();
      await controller.load();
      await controller.startProject('p1');

      controller.dismissActionError();
      expect(controller.actionError.value, isNull);
    });
  });

  group('after creation', () {
    test('the new project appears immediately and the list is re-read', () async {
      projects.projects = [testProject(id: 'p1')];
      final controller = build();
      await controller.load();
      final before = projects.listCount;

      final created = testProject(id: 'new', name: 'Fresh');
      projects.projects = [...projects.projects, created];
      await controller.onProjectCreated(created);

      expect(projects.listCount, before + 1);
      expect(controller.visibleProjects.map((p) => p.id), contains('new'));
    });
  });

  group('unpaged list', () {
    test('says so above the threshold, and not below it', () async {
      projects.projects = [
        for (var i = 0; i < ProjectsController.largeListThreshold; i++)
          testProject(id: 'p$i', name: 'Project $i'),
      ];
      final controller = build();
      await controller.load();
      expect(controller.isLargeList, isFalse);

      projects.projects = [...projects.projects, testProject(id: 'extra')];
      await controller.load();
      expect(controller.isLargeList, isTrue);
    });
  });
}
