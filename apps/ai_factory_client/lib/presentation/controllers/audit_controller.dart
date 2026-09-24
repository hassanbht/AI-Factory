import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/request_cancellation.dart';
import '../../core/state/view_state.dart';
import '../../domain/enums/backend_enums.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/audit.dart';
import '../../domain/repositories/repositories.dart';

/// Backs the audit log section.
///
/// Two filters with two very different mechanisms, and the UI is required to
/// keep them apart:
///
/// * **project** is a real query parameter — changing it re-queries the
///   server and resets to page 1.
/// * **severity** is client-side only. `GET /api/audit-log` accepts a
///   `severity` parameter, validates it, and then builds its query without
///   it, so it filters nothing (gap `G-AUDIT-SEVERITY`). Sending it would
///   claim a server-side filter that does not exist, so this controller
///   narrows the page already in memory and the UI says so.
class AuditController extends GetxController {
  AuditController({AuditRepository? audit, this.autoLoad = true})
    : _audit = audit ?? Get.find<AuditRepository>();

  /// The audit list sits below the operations sections; a screen that does
  /// not show it can construct this controller without issuing a request.
  final bool autoLoad;

  final AuditRepository _audit;

  /// The backend caps `pageSize` at 100; this stays under it because the list
  /// is read, not scrolled through in bulk.
  static const int pageSize = 50;

  final Rx<ViewState<PagedResult<AuditLogEntry>>> state =
      Rx<ViewState<PagedResult<AuditLogEntry>>>(
        const Idle<PagedResult<AuditLogEntry>>(),
      );

  /// Server-side filter. Null means every project.
  final Rxn<String> projectFilter = Rxn<String>();

  /// Client-side filter over the loaded page only.
  final Rxn<AuditSeverity> severityFilter = Rxn<AuditSeverity>();

  final RxInt page = 1.obs;

  RequestCancellation? _inFlight;

  bool get needsOperatorAccess {
    final current = state.value;
    return current is Failure<PagedResult<AuditLogEntry>> &&
        current.error is AuthenticationError;
  }

  /// The loaded page narrowed by [severityFilter] — never a server result.
  List<AuditLogEntry> get visibleEntries {
    final loaded = state.value.dataOrNull?.items ?? const <AuditLogEntry>[];
    final severity = severityFilter.value;
    if (severity == null) return loaded;
    return loaded.where((entry) => entry.severity == severity).toList();
  }

  @override
  void onInit() {
    super.onInit();
    if (autoLoad) load();
  }

  @override
  void onClose() {
    _inFlight?.cancel();
    _inFlight = null;
    super.onClose();
  }

  Future<void> load() => _fetch(refreshing: false);

  Future<void> refreshData() => _fetch(refreshing: true);

  /// Re-queries the server; the project id is a real filter parameter.
  Future<void> filterByProject(String? projectId) {
    projectFilter.value = projectId;
    page.value = 1;
    return _fetch(refreshing: true);
  }

  /// Narrows what is already on screen. No request is made, because the
  /// backend would ignore the parameter anyway.
  void filterBySeverity(AuditSeverity? severity) =>
      severityFilter.value = severity;

  Future<void> nextPage() {
    final current = state.value.dataOrNull;
    if (current == null || !current.hasMore) return Future<void>.value();
    page.value += 1;
    return _fetch(refreshing: true);
  }

  Future<void> previousPage() {
    if (page.value <= 1) return Future<void>.value();
    page.value -= 1;
    return _fetch(refreshing: true);
  }

  Future<void> _fetch({required bool refreshing}) async {
    _inFlight?.cancel();
    final cancellation = RequestCancellation();
    _inFlight = cancellation;

    final previous = state.value.dataOrNull;
    state.value = refreshing && previous != null
        ? Refreshing<PagedResult<AuditLogEntry>>(previous)
        : const Loading<PagedResult<AuditLogEntry>>();

    try {
      final result = await _audit.list(
        AuditLogQuery(
          projectId: projectFilter.value,
          page: page.value,
          pageSize: pageSize,
        ),
        cancellation: cancellation,
      );
      if (_inFlight != cancellation) return;
      state.value = result.items.isEmpty
          ? const Empty<PagedResult<AuditLogEntry>>()
          : Success<PagedResult<AuditLogEntry>>(result);
    } on RequestCancelledError {
      return;
    } on AppError catch (error) {
      if (_inFlight != cancellation) return;
      state.value = Failure<PagedResult<AuditLogEntry>>(error, stale: previous);
    } finally {
      if (_inFlight == cancellation) _inFlight = null;
    }
  }
}
