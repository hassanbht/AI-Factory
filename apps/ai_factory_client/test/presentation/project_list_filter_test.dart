import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/models/project_list_filter.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

/// The client-side filter and sort rules, tested directly.
///
/// They are client-side because `GET /api/projects` accepts no query
/// parameters at all — no paging, no status filter, no ordering. These tests
/// are the record of that, so nothing later mistakes them for something the
/// server did.
void main() {
  group('ProjectStatusFilter', () {
    test('every group maps onto real backend statuses', () {
      final byStatus = <ProjectStatus, ProjectStatusFilter>{
        ProjectStatus.created: ProjectStatusFilter.notStarted,
        ProjectStatus.implementation: ProjectStatusFilter.active,
        ProjectStatus.humanReview: ProjectStatusFilter.humanReview,
        ProjectStatus.paused: ProjectStatusFilter.paused,
        ProjectStatus.completed: ProjectStatusFilter.completed,
        ProjectStatus.failed: ProjectStatusFilter.failed,
        ProjectStatus.cancelled: ProjectStatusFilter.failed,
      };

      byStatus.forEach((status, expected) {
        final project = testProject(status: status);
        expect(
          expected.matches(project),
          isTrue,
          reason: '${status.wireValue} should match ${expected.name}',
        );
        expect(ProjectStatusFilter.all.matches(project), isTrue);
      });
    });

    test('every active lifecycle stage is in the active group', () {
      for (final status in ProjectStatus.values.where((s) => s.isActive)) {
        expect(
          ProjectStatusFilter.active.matches(testProject(status: status)),
          isTrue,
          reason: status.wireValue,
        );
      }
    });

    test('an unrecognized status falls into no group but the total', () {
      // It must stay visible under "All" rather than disappearing, and must
      // not be claimed by a group this client only guessed at.
      final project = testProject(
        status: ProjectStatus.unknown,
        rawStatus: 'Negotiating',
      );

      expect(ProjectStatusFilter.all.matches(project), isTrue);
      for (final filter in ProjectStatusFilter.values.where(
        (f) => f != ProjectStatusFilter.all,
      )) {
        expect(filter.matches(project), isFalse, reason: filter.name);
      }
    });

    test('every group has a translation key', () {
      for (final filter in ProjectStatusFilter.values) {
        expect(filter.labelKey, 'projects.filter.${filter.name}');
      }
    });
  });

  group('ProjectListFilter', () {
    final project = testProject(name: 'Customer API', slug: 'customer-api');

    test('an empty filter matches everything and reports itself inactive', () {
      expect(ProjectListFilter.none.isActive, isFalse);
      expect(ProjectListFilter.none.matches(project), isTrue);
    });

    test(
      'the query matches the name and the slug, ignoring case and padding',
      () {
        for (final query in <String>[
          'customer',
          'CUSTOMER',
          '  api  ',
          'customer-api',
        ]) {
          expect(
            ProjectListFilter(query: query).matches(project),
            isTrue,
            reason: query,
          );
        }
        expect(
          const ProjectListFilter(query: 'billing').matches(project),
          isFalse,
        );
      },
    );

    test('the id is deliberately not searchable', () {
      // A partial GUID match produces surprising results, and operators copy
      // ids whole rather than typing fragments of them.
      final byId = testProject(id: 'abcdef12-3456', name: 'Thing');
      expect(const ProjectListFilter(query: 'abcdef').matches(byId), isFalse);
    });

    test('status and query are both required to match', () {
      const filter = ProjectListFilter(
        status: ProjectStatusFilter.humanReview,
        query: 'customer',
      );

      expect(filter.isActive, isTrue);
      expect(filter.matches(testProject(name: 'Customer API')), isFalse);
      expect(
        filter.matches(
          testProject(name: 'Customer API', status: ProjectStatus.humanReview),
        ),
        isTrue,
      );
    });
  });

  group('ProjectSort', () {
    test('the rank is a UI grouping, not a business priority', () {
      // Anything still in motion, including Created and Paused, comes first
      // because those are the rows an operator acts on next.
      expect(ProjectSort.rank(testProject(status: ProjectStatus.created)), 0);
      expect(ProjectSort.rank(testProject(status: ProjectStatus.paused)), 0);
      expect(
        ProjectSort.rank(testProject(status: ProjectStatus.implementation)),
        0,
      );
      expect(
        ProjectSort.rank(testProject(status: ProjectStatus.humanReview)),
        1,
      );
      expect(ProjectSort.rank(testProject(status: ProjectStatus.completed)), 2);
      expect(ProjectSort.rank(testProject(status: ProjectStatus.failed)), 3);
      expect(ProjectSort.rank(testProject(status: ProjectStatus.cancelled)), 3);
      expect(ProjectSort.rank(testProject(status: ProjectStatus.unknown)), 4);
    });

    test('apply returns an unmodifiable list', () {
      final sorted = ProjectSort.apply([testProject()]);
      expect(() => sorted.add(testProject()), throwsUnsupportedError);
    });

    test('needsAttention honours the band limit', () {
      final many = [
        for (var i = 0; i < 10; i++)
          testProject(id: 'p$i', status: ProjectStatus.failed),
      ];

      expect(ProjectSort.needsAttention(many, take: 3), hasLength(3));
      expect(ProjectSort.needsAttention(many), hasLength(10));
    });

    test('a running project with a recorded error still needs attention', () {
      final projects = [
        testProject(id: 'running-ok', status: ProjectStatus.implementation),
        testProject(
          id: 'running-err',
          status: ProjectStatus.implementation,
          currentError: 'build failed',
        ),
      ];

      expect(ProjectSort.needsAttention(projects).map((p) => p.id), <String>[
        'running-err',
      ]);
    });

    test('an empty currentError is not an error', () {
      // The backend writes an empty string as well as null; neither is a
      // reason to put a project in front of a person.
      final projects = [
        testProject(
          id: 'p1',
          status: ProjectStatus.implementation,
          currentError: '',
        ),
      ];

      expect(ProjectSort.needsAttention(projects), isEmpty);
    });

    test('mostRecent orders by last update, newest first', () {
      final projects = [
        testProject(id: 'a', updatedAtUtc: DateTime.utc(2026, 9, 17)),
        testProject(id: 'c', updatedAtUtc: DateTime.utc(2026, 9, 19)),
        testProject(id: 'b', updatedAtUtc: DateTime.utc(2026, 9, 18)),
      ];

      expect(ProjectSort.mostRecent(projects).map((p) => p.id), <String>[
        'c',
        'b',
        'a',
      ]);
      expect(
        ProjectSort.mostRecent(projects, take: 2).map((p) => p.id),
        <String>['c', 'b'],
      );
    });
  });
}
