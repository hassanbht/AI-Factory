import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';
import '../../domain/repositories/repositories.dart';
import 'project_detail_controller.dart';

/// Backs the Human Review section of Project Detail.
///
/// This controller owns exactly one thing: the approval mutation.
/// It does **not** fetch the project, work items or agent runs — those are
/// loaded by the sibling controllers already bound to the same route, and
/// re-fetching them here would violate Workstream AH.
///
/// Approval flow:
///
///   1. Operator taps "Approve project".
///   2. Confirmation dialog confirmed.
///   3. `approveProject()` is called.
///   4. Guard: if `isApproving`, the second call is dropped.
///   5. Guard: if `project.status != HumanReview`, error shown and API
///      not called — the client never calls the approval endpoint for a
///      project that is not in the correct state.
///   6. `POST /api/projects/{id}/approve` is sent.
///   7. Success → `ProjectDetailController.refreshData()` — the server
///      response is authoritative; never locally set `Completed`.
///   8. `ConflictError` or `NotFoundError` → error shown + refresh.
///   9. Any other `AppError` → error shown, no refresh.
///
/// The controller is registered in the route binding and disposed on
/// route exit, which cancels any in-flight approval.
class HumanReviewController extends GetxController {
  HumanReviewController({
    required this.projectId,
    ApprovalRepository? approval,
    ProjectDetailController? detail,
  }) : _approval = approval ?? Get.find<ApprovalRepository>(),
       _detail = detail ?? Get.find<ProjectDetailController>();

  final String projectId;
  final ApprovalRepository _approval;
  final ProjectDetailController _detail;

  /// True while the approval POST is in flight.
  ///
  /// The widget disables the button on this; the controller also drops a
  /// second call — so the button alone is not the only guard.
  final RxBool isApproving = false.obs;

  /// The last failed approval action — shown as a banner above the section.
  ///
  /// A rejected approval never blanks out the evidence already on screen,
  /// so this is separate from the Project Detail screen state.
  final Rxn<AppError> approvalError = Rxn<AppError>();

  /// True after the server confirmed `Completed`.
  ///
  /// Set only after [ProjectDetailController.refreshData] returns — never
  /// before, and never based on an optimistic local assumption.
  final RxBool approvalSuccess = false.obs;

  RequestCancellation? _inFlight;

  /// The project the sibling controller currently holds, or `null` when the
  /// detail screen has not loaded yet.
  Project? get _project => _detail.project;

  /// Whether the Approve action is currently eligible.
  ///
  /// `true` only when the project is in `HumanReview` and no approval is in
  /// flight. The backend is the final authority; this is a display gate only.
  bool get canApprove {
    final p = _project;
    return p != null &&
        p.status == ProjectStatus.humanReview &&
        !isApproving.value;
  }

  @override
  void onClose() {
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  void dismissApprovalError() => approvalError.value = null;

  /// Submits the approval to the backend.
  ///
  /// [actor] is an unauthenticated audit label the API records verbatim; it
  /// is never verified and never used for authorization.
  ///
  /// A second call while the first is in flight is silently dropped.
  /// The controller **never** locally sets `project.status = Completed`.
  Future<void> approveProject({String? actor}) async {
    // Duplicate-submission guard — the controller owns this, not just the UI.
    if (isApproving.value) return;

    // Client-side eligibility check. The backend enforces this too (409),
    // but calling it for a project clearly not in HumanReview is wasteful.
    final project = _project;
    if (project == null || project.status != ProjectStatus.humanReview) {
      approvalError.value = const ConflictError(
        technicalDetail: 'Project is not in HumanReview status.',
      );
      return;
    }

    isApproving.value = true;
    approvalError.value = null;

    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    try {
      // The server validates HumanReview state and returns the Completed
      // project. We discard the returned project here and instead call
      // refreshData() so the Detail screen holds exactly what the server
      // says — not a copy we manufactured.
      await _approval.approve(
        projectId,
        actor: actor,
        cancellation: cancellation,
      );

      if (_inFlight != cancellation) return;
      _inFlight = null;

      // Reload from server — the only authoritative completion sequence.
      await _detail.refreshData();

      // Mark success only after the reload confirms the new state.
      approvalSuccess.value = true;
    } on RequestCancelledError {
      // Route was closed mid-flight; state is irrelevant.
      return;
    } on ConflictError catch (error) {
      if (_inFlight != cancellation) return;
      _inFlight = null;
      approvalError.value = error;
      // 409 means the project state changed; reload so the operator sees
      // what the server actually holds, not our stale copy.
      await _detail.refreshData();
    } on NotFoundError catch (error) {
      if (_inFlight != cancellation) return;
      _inFlight = null;
      approvalError.value = error;
      // 404 also means the client's copy is wrong.
      await _detail.refreshData();
    } on AppError catch (error) {
      if (_inFlight != cancellation) return;
      _inFlight = null;
      approvalError.value = error;
      // Network, timeout, server — no refresh; operator can retry.
    } finally {
      if (_inFlight == cancellation) {
        _inFlight = null;
        isApproving.value = false;
      } else {
        // Already cleared by the cancellation branch above.
        isApproving.value = false;
      }
    }
  }
}
