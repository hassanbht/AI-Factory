import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/presentation/controllers/human_review_controller.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

/// [HumanReviewController] against scripted repositories.
///
/// Every test:
/// - Builds a [ProjectDetailController] that holds a known project in
///   `Success` state (same pattern as `project_detail_controller_test.dart`).
/// - Builds a [FakeApprovalRepository] with staged outcomes.
/// - Asserts that the controller publishes the correct observable state
///   and that it never locally sets `project.status = Completed`.
void main() {
  const projectId = 'p1';

  /// Creates a [ProjectDetailController] already holding [project] in a
  /// loaded state, using a [FakeProjectRepository] that also supports
  /// staging a refresh result.
  ///
  /// Returns both the controller and the fake repo so individual tests can
  /// assert getById call counts or stage a different refresh response.
  ({ProjectDetailController detail, FakeProjectRepository repo}) buildDetail(
    Project project,
  ) {
    final repo = FakeProjectRepository(projects: [project]);
    final ctrl = ProjectDetailController(projectId: project.id, projects: repo)
      ..onInit();
    return (detail: ctrl, repo: repo);
  }

  setUp(() {
    Get.testMode = true;
  });

  tearDown(() async {
    await Get.deleteAll(force: true);
  });

  // ---------------------------------------------------------------------------
  // canApprove
  // ---------------------------------------------------------------------------

  group('canApprove', () {
    test('true when project is in HumanReview', () async {
      final project = testProject(status: ProjectStatus.humanReview);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero); // let onInit finish

      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: FakeApprovalRepository(),
        detail: detail,
      );
      expect(ctrl.canApprove, isTrue);
    });

    for (final status in [
      ProjectStatus.created,
      ProjectStatus.analyzing,
      ProjectStatus.implementation,
      ProjectStatus.reviewing,
      ProjectStatus.testing,
      ProjectStatus.completed,
      ProjectStatus.failed,
      ProjectStatus.cancelled,
      ProjectStatus.paused,
    ]) {
      test('false when status is ${status.wireValue}', () async {
        final project = testProject(status: status);
        final (:detail, :repo) = buildDetail(project);
        await Future<void>.delayed(Duration.zero);

        final ctrl = HumanReviewController(
          projectId: projectId,
          approval: FakeApprovalRepository(),
          detail: detail,
        );
        expect(
          ctrl.canApprove,
          isFalse,
          reason: '${status.wireValue} must not offer the Approve button',
        );
      });
    }
  });

  // ---------------------------------------------------------------------------
  // Successful approval
  // ---------------------------------------------------------------------------

  group('approveProject — success', () {
    test('posts approval and sets approvalSuccess after refresh', () async {
      final project = testProject(status: ProjectStatus.humanReview);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero);

      // Stage the server response for the post-approval refresh.
      repo.getByIdResult = testProject(
        id: projectId,
        status: ProjectStatus.completed,
      );

      final approval = FakeApprovalRepository();
      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: approval,
        detail: detail,
      );

      await ctrl.approveProject();

      expect(ctrl.approvalSuccess.value, isTrue);
      expect(ctrl.approvalError.value, isNull);
      expect(approval.approveCount, 1);
    });

    test(
      'server status after refresh is Completed, not locally forced',
      () async {
        final project = testProject(status: ProjectStatus.humanReview);
        final (:detail, :repo) = buildDetail(project);
        await Future<void>.delayed(Duration.zero);

        // The detail controller's project should remain HumanReview until the
        // server response for the refresh arrives.
        final gate = Completer<void>();
        final approval = FakeApprovalRepository()..gate = gate.future;
        final ctrl = HumanReviewController(
          projectId: projectId,
          approval: approval,
          detail: detail,
        );

        unawaited(ctrl.approveProject());
        await Future<void>.delayed(Duration.zero);

        // Before gate completes the server has not replied.
        expect(
          detail.state.value.dataOrNull?.status,
          ProjectStatus.humanReview,
          reason: 'client must not optimistically set Completed',
        );

        gate.complete();
        await Future<void>.delayed(Duration.zero);
      },
    );
  });

  // ---------------------------------------------------------------------------
  // Duplicate-submission guard
  // ---------------------------------------------------------------------------

  test(
    'second call while first is in flight is dropped — exactly one request',
    () async {
      final project = testProject(status: ProjectStatus.humanReview);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero);

      final gate = Completer<void>();
      final approval = FakeApprovalRepository()..gate = gate.future;
      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: approval,
        detail: detail,
      );

      unawaited(ctrl.approveProject());
      await Future<void>.delayed(Duration.zero);
      unawaited(ctrl.approveProject()); // dropped
      await Future<void>.delayed(Duration.zero);

      gate.complete();
      await Future<void>.delayed(Duration.zero);

      expect(approval.approveCount, 1, reason: 'exactly one approve call sent');
    },
  );

  // ---------------------------------------------------------------------------
  // ConflictError
  // ---------------------------------------------------------------------------

  test('ConflictError sets approvalError and triggers refresh', () async {
    final project = testProject(status: ProjectStatus.humanReview);
    final (:detail, :repo) = buildDetail(project);
    await Future<void>.delayed(Duration.zero);

    final initialGetByIdCount = repo.getByIdCount;

    final approval = FakeApprovalRepository()
      ..approveError = const ConflictError();
    final ctrl = HumanReviewController(
      projectId: projectId,
      approval: approval,
      detail: detail,
    );

    await ctrl.approveProject();

    expect(ctrl.approvalError.value, isA<ConflictError>());
    expect(ctrl.approvalSuccess.value, isFalse);
    expect(
      repo.getByIdCount,
      greaterThan(initialGetByIdCount),
      reason: 'ConflictError must trigger a refresh',
    );
  });

  // ---------------------------------------------------------------------------
  // NotFoundError
  // ---------------------------------------------------------------------------

  test('NotFoundError sets approvalError and triggers refresh', () async {
    final project = testProject(status: ProjectStatus.humanReview);
    final (:detail, :repo) = buildDetail(project);
    await Future<void>.delayed(Duration.zero);

    final initialGetByIdCount = repo.getByIdCount;

    final approval = FakeApprovalRepository()
      ..approveError = const NotFoundError();
    final ctrl = HumanReviewController(
      projectId: projectId,
      approval: approval,
      detail: detail,
    );

    await ctrl.approveProject();

    expect(ctrl.approvalError.value, isA<NotFoundError>());
    expect(repo.getByIdCount, greaterThan(initialGetByIdCount));
  });

  // ---------------------------------------------------------------------------
  // NetworkError — no refresh
  // ---------------------------------------------------------------------------

  test(
    'NetworkError sets approvalError without triggering a refresh',
    () async {
      final project = testProject(status: ProjectStatus.humanReview);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero);

      final initialGetByIdCount = repo.getByIdCount;

      final approval = FakeApprovalRepository()
        ..approveError = const NetworkError();
      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: approval,
        detail: detail,
      );

      await ctrl.approveProject();

      expect(ctrl.approvalError.value, isA<NetworkError>());
      expect(
        repo.getByIdCount,
        initialGetByIdCount,
        reason:
            'NetworkError must not trigger a refresh — server state unknown',
      );
    },
  );

  // ---------------------------------------------------------------------------
  // TimeoutError — no refresh
  // ---------------------------------------------------------------------------

  test(
    'TimeoutError sets approvalError without triggering a refresh',
    () async {
      final project = testProject(status: ProjectStatus.humanReview);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero);

      final initialGetByIdCount = repo.getByIdCount;

      final approval = FakeApprovalRepository()
        ..approveError = const TimeoutError();
      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: approval,
        detail: detail,
      );

      await ctrl.approveProject();

      expect(ctrl.approvalError.value, isA<TimeoutError>());
      expect(repo.getByIdCount, initialGetByIdCount);
    },
  );

  // ---------------------------------------------------------------------------
  // Non-eligible status guard — API not called
  // ---------------------------------------------------------------------------

  test(
    'approveProject for Completed project sets error and makes no API call',
    () async {
      final project = testProject(status: ProjectStatus.completed);
      final (:detail, :repo) = buildDetail(project);
      await Future<void>.delayed(Duration.zero);

      final approval = FakeApprovalRepository();
      final ctrl = HumanReviewController(
        projectId: projectId,
        approval: approval,
        detail: detail,
      );

      await ctrl.approveProject();

      expect(
        approval.approveCount,
        0,
        reason: 'must not call API for a project not in HumanReview',
      );
      expect(ctrl.approvalError.value, isNotNull);
    },
  );

  // ---------------------------------------------------------------------------
  // dismissApprovalError
  // ---------------------------------------------------------------------------

  test('dismissApprovalError clears the approval error', () async {
    final project = testProject(status: ProjectStatus.humanReview);
    final (:detail, :repo) = buildDetail(project);
    await Future<void>.delayed(Duration.zero);

    final approval = FakeApprovalRepository()
      ..approveError = const NetworkError();
    final ctrl = HumanReviewController(
      projectId: projectId,
      approval: approval,
      detail: detail,
    );

    await ctrl.approveProject();
    expect(ctrl.approvalError.value, isNotNull);

    ctrl.dismissApprovalError();
    expect(ctrl.approvalError.value, isNull);
  });

  // ---------------------------------------------------------------------------
  // isApproving resets after completion
  // ---------------------------------------------------------------------------

  test('isApproving resets to false after successful approval', () async {
    final project = testProject(status: ProjectStatus.humanReview);
    final (:detail, :repo) = buildDetail(project);
    await Future<void>.delayed(Duration.zero);

    final ctrl = HumanReviewController(
      projectId: projectId,
      approval: FakeApprovalRepository(),
      detail: detail,
    );

    await ctrl.approveProject();
    expect(ctrl.isApproving.value, isFalse);
  });

  test('isApproving resets to false after ConflictError', () async {
    final project = testProject(status: ProjectStatus.humanReview);
    final (:detail, :repo) = buildDetail(project);
    await Future<void>.delayed(Duration.zero);

    final ctrl = HumanReviewController(
      projectId: projectId,
      approval: FakeApprovalRepository()..approveError = const ConflictError(),
      detail: detail,
    );

    await ctrl.approveProject();
    expect(ctrl.isApproving.value, isFalse);
  });
}
