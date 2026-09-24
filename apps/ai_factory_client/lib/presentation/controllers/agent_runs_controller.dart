import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../data/dto/requests.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/repositories/repositories.dart';
import '../models/agent_runs_data.dart';

/// Backs the AgentRuns section of Project Detail.
///
/// `GET /api/agents/runs` is the only paged listing the API has (§6 of
/// `docs/flutter-api-contract.md`), so this reads one page of the project's
/// runs, joined with `GET /api/agents/definitions` for the agent name/type
/// each run does not itself carry. Status is a real server-side filter;
/// there is no `workItemId` filter (gap `G-WORKITEM-EVENTS`), so
/// [filterByWorkItem] narrows the already-fetched page instead of issuing a
/// second request.
class AgentRunsController extends GetxController {
  AgentRunsController({required this.projectId, AgentRunRepository? agentRuns})
    : _agentRuns = agentRuns ?? Get.find<AgentRunRepository>();

  final String projectId;
  final AgentRunRepository _agentRuns;

  /// The backend rejects a page size above 100; one page is enough for a
  /// single-project console and avoids the redundant per-row detail fetches
  /// Workstream AH warns against — the list endpoint already returns each
  /// run's full structured result.
  static const int _pageSize = 100;

  final Rx<ViewState<AgentRunsData>> state = Rx<ViewState<AgentRunsData>>(
    const Idle<AgentRunsData>(),
  );

  final Rxn<String> selectedAgentRunId = Rxn<String>();
  final Rxn<AgentRunStatus> statusFilter = Rxn<AgentRunStatus>();

  /// Client-side only — narrows [AgentRunsData.runs] to one work item without
  /// a second request. Set from a WorkItem's "View agent runs" action.
  final Rxn<String> workItemFilter = Rxn<String>();

  RequestCancellation? _inFlight;

  @override
  void onInit() {
    super.onInit();
    load();
  }

  @override
  void onClose() {
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  Future<void> load() => _fetch(refreshing: false);

  Future<void> refreshData() => _fetch(refreshing: true);

  /// One read inside Project Detail's coordinated refresh cycle. Returns the
  /// error, or null on success. The current status filter is kept, so a poll
  /// never widens what the operator narrowed. No timer lives here.
  Future<AppError?> poll({required bool silent}) =>
      _fetch(refreshing: true, silent: silent);

  /// A run that is queued or executing, as last reported — the signal for
  /// the fastest Project Detail interval.
  bool get hasActiveRuns =>
      state.value.dataOrNull?.runs.any(
        (run) => switch (run.status) {
          AgentRunStatus.queued ||
          AgentRunStatus.running ||
          AgentRunStatus.waiting => true,
          _ => false,
        },
      ) ??
      false;

  void selectAgentRun(String? id) => selectedAgentRunId.value = id;

  /// Re-fetches with the status query parameter — a real server-side filter,
  /// unlike [filterByWorkItem].
  Future<void> setStatusFilter(AgentRunStatus? status) {
    statusFilter.value = status;
    return _fetch(refreshing: true);
  }

  void filterByWorkItem(String? workItemId) {
    workItemFilter.value = workItemId;
    selectedAgentRunId.value = null;
  }

  Future<AppError?> _fetch({
    required bool refreshing,
    bool silent = false,
  }) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    if (!silent) {
      state.value = refreshing && previous != null
          ? Refreshing<AgentRunsData>(previous)
          : const Loading<AgentRunsData>();
    }

    try {
      final page = await _agentRuns.list(
        AgentRunQuery(
          projectId: projectId,
          status: statusFilter.value,
          pageSize: _pageSize,
        ),
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return const RequestCancelledError();

      // Agent definitions are configuration, not live state: a background
      // poll reuses the ones already loaded rather than spending a request of
      // the shared rate-limit budget on them every few seconds. A manual
      // refresh re-reads them.
      final known = previous?.definitionsById;
      final Map<String, AgentDefinition> definitionsById;
      if (silent && known != null && known.isNotEmpty) {
        definitionsById = known;
      } else {
        final definitions = await _agentRuns.listDefinitions(
          cancellation: cancellation,
        );
        if (_inFlight != cancellation) return const RequestCancelledError();
        definitionsById = {for (final d in definitions) d.id: d};
      }

      _inFlight = null;
      final data = AgentRunsData(
        runs: page.items,
        definitionsById: definitionsById,
        totalCount: page.totalCount,
      );
      state.value = data.isEmpty
          ? const Empty<AgentRunsData>()
          : Success<AgentRunsData>(data);
      return null;
    } on RequestCancelledError catch (error) {
      return error;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return const RequestCancelledError();
      _inFlight = null;
      state.value = Failure<AgentRunsData>(error, stale: previous);
      return error;
    }
  }
}
