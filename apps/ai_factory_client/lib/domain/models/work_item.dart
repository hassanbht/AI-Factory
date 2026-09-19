import 'package:equatable/equatable.dart';

import '../enums/backend_enums.dart';

/// One executable unit of work inside a project.
///
/// Fields the API does not return are absent rather than defaulted:
/// there is no `maxAttempts` (gap `G-MAXATTEMPTS`), so [attemptCount] is
/// shown as a bare number, and no dependency list (gap `G-DEPS`), which is
/// why [WorkItemNode] exists separately.
class WorkItem extends Equatable {
  const WorkItem({
    required this.id,
    required this.projectId,
    required this.type,
    required this.rawType,
    required this.title,
    required this.status,
    required this.rawStatus,
    required this.priority,
    required this.rawPriority,
    required this.attemptCount,
    required this.repairAttemptCount,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
    this.currentError,
    this.lastHeartbeatAtUtc,
  });

  final String id;
  final String projectId;

  final WorkItemType type;
  final String rawType;

  final String title;
  final String? description;

  final WorkItemStatus status;

  /// The backend column is a free-form string, so an unrecognized value is a
  /// realistic possibility rather than a theoretical one.
  final String rawStatus;

  final WorkItemPriority priority;
  final String rawPriority;

  final int attemptCount;
  final int repairAttemptCount;
  final String? currentError;

  /// Last sign of life from the executing worker.
  final DateTime? lastHeartbeatAtUtc;

  final DateTime createdAtUtc;
  final DateTime updatedAtUtc;

  bool get hasBeenRepaired => repairAttemptCount > 0;

  bool get hasError => currentError != null && currentError!.isNotEmpty;

  @override
  List<Object?> get props => [
    id,
    projectId,
    type,
    rawType,
    title,
    description,
    status,
    rawStatus,
    priority,
    rawPriority,
    attemptCount,
    repairAttemptCount,
    currentError,
    lastHeartbeatAtUtc,
    createdAtUtc,
    updatedAtUtc,
  ];
}

/// A work item plus the plan information that carries its dependency edges.
///
/// The API splits this across two endpoints — `GET .../tasks` has the live
/// state, `GET .../plan` has `dependsOn` — so the join happens here rather
/// than in a widget.
class WorkItemNode extends Equatable {
  const WorkItemNode({
    required this.workItem,
    required this.planKey,
    required this.dependsOnPlanKeys,
    required this.dependsOnWorkItemIds,
    required this.readiness,
  });

  final WorkItem workItem;

  /// `T01`-style identifier from the plan.
  final String planKey;

  final List<String> dependsOnPlanKeys;

  /// The same edges resolved to work item ids, for navigation.
  final List<String> dependsOnWorkItemIds;

  /// Readiness as the plan reported it. `GET /api/tasks/{id}/readiness`
  /// returns the live value for a single item.
  final TaskReadiness readiness;

  bool get isRoot => dependsOnPlanKeys.isEmpty;

  @override
  List<Object?> get props => [
    workItem,
    planKey,
    dependsOnPlanKeys,
    dependsOnWorkItemIds,
    readiness,
  ];
}

/// The plan, as the DAG the UI renders.
class ProjectPlan extends Equatable {
  const ProjectPlan({
    required this.projectId,
    required this.planHash,
    required this.nodes,
  });

  final String projectId;

  /// Identifies the accepted task set; re-planning with a different set is
  /// rejected by the backend.
  final String planHash;

  final List<WorkItemNode> nodes;

  List<WorkItemNode> get roots =>
      nodes.where((node) => node.isRoot).toList(growable: false);

  /// Direct dependents of [planKey].
  List<WorkItemNode> childrenOf(String planKey) => nodes
      .where((node) => node.dependsOnPlanKeys.contains(planKey))
      .toList(growable: false);

  @override
  List<Object?> get props => [projectId, planHash, nodes];
}
