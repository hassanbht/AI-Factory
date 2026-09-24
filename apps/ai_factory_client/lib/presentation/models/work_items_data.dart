import 'package:equatable/equatable.dart';

import '../../domain/models/work_item.dart';
import 'dependency_graph_validation.dart';

/// What the WorkItems section renders, joined from whichever endpoints
/// actually answered.
///
/// [plan] is null whenever `GET /api/projects/{id}/plan` returned 404 (no
/// plan exists yet) — the section then falls back to [workItems] as a flat
/// list, exactly as `docs/flutter-ux-spec.md` §8 specifies, and never guesses
/// an edge. [warnings] is always computed from [plan] when one exists, so a
/// malformed edge is visible without the tree needing to detect it itself.
class WorkItemsData extends Equatable {
  const WorkItemsData({
    required this.workItems,
    required this.plan,
    required this.warnings,
  });

  factory WorkItemsData.flat(List<WorkItem> workItems) => WorkItemsData(
    workItems: List<WorkItem>.unmodifiable(workItems),
    plan: null,
    warnings: const <DependencyWarning>[],
  );

  factory WorkItemsData.withPlan(List<WorkItem> workItems, ProjectPlan plan) =>
      WorkItemsData(
        workItems: List<WorkItem>.unmodifiable(workItems),
        plan: plan,
        warnings: validateDependencyGraph(plan),
      );

  final List<WorkItem> workItems;
  final ProjectPlan? plan;
  final List<DependencyWarning> warnings;

  bool get hasPlan => plan != null;

  bool get isEmpty => workItems.isEmpty;

  @override
  List<Object?> get props => [workItems, plan, warnings];
}
