import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/presentation/models/project_lifecycle.dart';
import 'package:ai_factory_client/presentation/models/project_source.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

/// The lifecycle and source projections, as pure data.
///
/// These are the tests that hold the phase's central promise: the screen
/// states the project's *position*, and never invents the history the API does
/// not expose.
void main() {
  ProjectLifecycle lifecycleOf(
    ProjectStatus status, {
    bool startedAt = true,
    String? rawStatus,
  }) => ProjectLifecycle.of(
    testProject(
      status: status,
      rawStatus: rawStatus,
      lifecycleStartedAtUtc: startedAt ? DateTime.utc(2026, 9, 19, 12) : null,
    ),
  );

  group('the track', () {
    test('is the backend forward chain, in backend order', () {
      expect(ProjectLifecycle.track, <ProjectStatus>[
        ProjectStatus.created,
        ProjectStatus.analyzing,
        ProjectStatus.specification,
        ProjectStatus.architecture,
        ProjectStatus.planning,
        ProjectStatus.implementation,
        ProjectStatus.testing,
        ProjectStatus.reviewing,
        ProjectStatus.humanReview,
        ProjectStatus.completed,
      ]);
    });

    test('excludes every state that is not a step forward', () {
      for (final status in <ProjectStatus>[
        ProjectStatus.repairing,
        ProjectStatus.paused,
        ProjectStatus.failed,
        ProjectStatus.cancelled,
        ProjectStatus.unknown,
      ]) {
        expect(
          ProjectLifecycle.track,
          isNot(contains(status)),
          reason: '$status is not a forward stage',
        );
      }
    });
  });

  group('an on-track state', () {
    test('marks exactly one stage as current', () {
      for (final status in ProjectLifecycle.track) {
        final lifecycle = lifecycleOf(status);
        final current = lifecycle.nodes
            .where((n) => n.position == LifecyclePosition.current)
            .toList();

        expect(current, hasLength(1), reason: '$status');
        expect(current.single.status, status);
        expect(lifecycle.offTrack, isNull);
        expect(lifecycle.hasNoCurrentStage, isFalse);
      }
    });

    test('places the other stages before or after it, never "completed"', () {
      final lifecycle = lifecycleOf(ProjectStatus.implementation);
      final byStatus = <ProjectStatus, LifecyclePosition>{
        for (final node in lifecycle.nodes) node.status: node.position,
      };

      expect(byStatus[ProjectStatus.created], LifecyclePosition.earlier);
      expect(byStatus[ProjectStatus.planning], LifecyclePosition.earlier);
      expect(byStatus[ProjectStatus.implementation], LifecyclePosition.current);
      expect(byStatus[ProjectStatus.testing], LifecyclePosition.later);
      expect(byStatus[ProjectStatus.completed], LifecyclePosition.later);

      // The distinction the phase turns on: an earlier stage is "before the
      // current one", never "completed" — the API reported no such thing.
      expect(
        lifecycle.nodes.map((n) => n.position).toSet(),
        <LifecyclePosition>{
          LifecyclePosition.earlier,
          LifecyclePosition.current,
          LifecyclePosition.later,
        },
      );
    });

    test('Completed is the end of the track and is terminal', () {
      final lifecycle = lifecycleOf(ProjectStatus.completed);

      expect(lifecycle.currentNode?.status, ProjectStatus.completed);
      expect(lifecycle.currentNode?.index, ProjectLifecycle.track.length - 1);
      expect(lifecycle.isTerminal, isTrue);
      expect(
        lifecycle.nodes.where((n) => n.position == LifecyclePosition.later),
        isEmpty,
      );
    });

    test('every node knows its step number', () {
      final lifecycle = lifecycleOf(ProjectStatus.created);
      expect(lifecycle.nodes.first.index, 0);
      expect(lifecycle.nodes.last.index, ProjectLifecycle.track.length - 1);
    });
  });

  group('Repairing', () {
    test('marks no stage as current and anchors on Testing', () {
      final lifecycle = lifecycleOf(ProjectStatus.repairing);

      expect(lifecycle.offTrack, OffTrackState.repairing);
      expect(lifecycle.currentNode, isNull);
      expect(lifecycle.anchorNode?.status, ProjectStatus.testing);
    });

    test('keeps the stages either side of Testing ordered', () {
      final lifecycle = lifecycleOf(ProjectStatus.repairing);
      final byStatus = <ProjectStatus, LifecyclePosition>{
        for (final node in lifecycle.nodes) node.status: node.position,
      };

      expect(byStatus[ProjectStatus.implementation], LifecyclePosition.earlier);
      expect(byStatus[ProjectStatus.testing], LifecyclePosition.anchor);
      expect(byStatus[ProjectStatus.reviewing], LifecyclePosition.later);
    });
  });

  group('a state that overwrites the stage', () {
    test('leaves every position undetermined rather than guessing', () {
      const overwriting = <ProjectStatus, OffTrackState>{
        ProjectStatus.paused: OffTrackState.paused,
        ProjectStatus.failed: OffTrackState.failed,
        ProjectStatus.cancelled: OffTrackState.cancelled,
      };

      overwriting.forEach((status, expected) {
        final lifecycle = lifecycleOf(status);

        expect(lifecycle.offTrack, expected, reason: '$status');
        expect(lifecycle.currentNode, isNull, reason: '$status');
        expect(lifecycle.anchorNode, isNull, reason: '$status');
        expect(
          lifecycle.nodes.every(
            (n) => n.position == LifecyclePosition.undetermined,
          ),
          isTrue,
          reason: '$status must not imply a stage the API did not report',
        );
      });
    });

    test('an unrecognized wire value stays visible and undetermined', () {
      final lifecycle = lifecycleOf(
        ProjectStatus.unknown,
        rawStatus: 'Rehearsing',
      );

      expect(lifecycle.offTrack, OffTrackState.unrecognized);
      expect(lifecycle.rawStatus, 'Rehearsing');
      expect(lifecycle.currentNode, isNull);
    });
  });

  test('the started marker is carried through untouched', () {
    expect(
      lifecycleOf(ProjectStatus.created, startedAt: false).isStarted,
      isFalse,
    );
    expect(lifecycleOf(ProjectStatus.analyzing).isStarted, isTrue);
  });

  group('the source projection', () {
    test('a project still in Created has made no source decision', () {
      final source = ProjectSourceView.of(
        testProject(status: ProjectStatus.created),
      );
      expect(source.kind, ProjectSourceKind.notInitialized);
      expect(source.isGit, isFalse);
    });

    test('an initialized project with no repository is an empty workspace', () {
      final source = ProjectSourceView.of(
        testProject(status: ProjectStatus.analyzing),
      );
      expect(source.kind, ProjectSourceKind.empty);
    });

    test('a recorded repository is a git source', () {
      final source = ProjectSourceView.of(
        testProject(
          status: ProjectStatus.analyzing,
          gitRepository: 'https://github.com/acme/api.git',
          gitBranch: 'main',
        ),
      );

      expect(source.kind, ProjectSourceKind.gitRepository);
      expect(source.repository, 'https://github.com/acme/api.git');
      expect(source.branch, 'main');
    });

    test('a blank branch means "the repository default", not an empty one', () {
      final source = ProjectSourceView.of(
        testProject(
          status: ProjectStatus.analyzing,
          gitRepository: 'https://github.com/acme/api.git',
          gitBranch: '   ',
        ),
      );
      expect(source.branch, isNull);
    });

    group('credential redaction', () {
      test('drops userinfo carrying a token', () {
        expect(
          ProjectSourceView.redactCredentials(
            'https://x-access-token:ghp_secretvalue@github.com/acme/api.git',
          ),
          'https://github.com/acme/api.git',
        );
        expect(
          ProjectSourceView.redactCredentials(
            'https://user:pw@git.example.com/a/b.git',
          ),
          'https://git.example.com/a/b.git',
        );
      });

      test('a redacted URL reaches the widget tree already redacted', () {
        final source = ProjectSourceView.of(
          testProject(
            status: ProjectStatus.analyzing,
            gitRepository: 'https://u:ghp_tok@github.com/acme/api.git',
          ),
        );
        expect(source.repository, 'https://github.com/acme/api.git');
        expect(source.repository, isNot(contains('ghp_tok')));
      });

      test('leaves everything that is not a credential alone', () {
        const untouched = <String>[
          'https://github.com/acme/api.git',
          'git@github.com:acme/api.git',
          'ssh://git@github.com/acme/api.git',
          r'D:\repos\api',
          'file:///srv/git/api.git',
        ];
        for (final value in untouched) {
          expect(ProjectSourceView.redactCredentials(value), value);
        }
      });
    });
  });
}
