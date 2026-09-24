import 'package:equatable/equatable.dart';

import '../../domain/models/work_item.dart';

/// What can be wrong with a [ProjectPlan]'s edges.
///
/// The plan is assembled client-side from two endpoints (`G-DEPS`), so a race
/// between them, a malformed `dependsOn` entry, or a genuine backend defect
/// are all realistic — the tree renderer must survive every one of them
/// rather than recurse forever or throw.
enum DependencyWarningKind {
  selfDependency,
  duplicateDependency,
  duplicateWorkItem,
  unknownDependency,
  cycle,
}

class DependencyWarning extends Equatable {
  const DependencyWarning({
    required this.kind,
    required this.planKey,
    this.relatedPlanKey,
  });

  final DependencyWarningKind kind;

  /// The plan key the problem was found on.
  final String planKey;

  /// The other plan key involved, when the warning names one (the duplicate
  /// or unknown target, or the node that closes a cycle).
  final String? relatedPlanKey;

  @override
  List<Object?> get props => [kind, planKey, relatedPlanKey];
}

/// Validates a plan's edges without changing them.
///
/// [ProjectPlan]'s own mapper already drops a plan entry whose work item is
/// missing (see `mapProjectPlan`), so this checks what can still be wrong
/// once every node has a live work item: a node depending on itself, the same
/// dependency listed twice, two nodes sharing a plan key or work item id, an
/// edge naming a plan key nothing in the plan owns, and a dependency cycle.
///
/// Nothing is deleted here — the valid portion renders exactly as before, and
/// the result is additional information for a UI that wants to say so.
List<DependencyWarning> validateDependencyGraph(ProjectPlan plan) {
  final warnings = <DependencyWarning>[];
  final planKeys = <String>{};
  final workItemIds = <String>{};

  for (final node in plan.nodes) {
    if (!planKeys.add(node.planKey)) {
      warnings.add(
        DependencyWarning(
          kind: DependencyWarningKind.duplicateWorkItem,
          planKey: node.planKey,
        ),
      );
    }
    if (!workItemIds.add(node.workItem.id)) {
      warnings.add(
        DependencyWarning(
          kind: DependencyWarningKind.duplicateWorkItem,
          planKey: node.planKey,
          relatedPlanKey: node.workItem.id,
        ),
      );
    }
  }

  for (final node in plan.nodes) {
    final seenDeps = <String>{};
    for (final dep in node.dependsOnPlanKeys) {
      if (dep == node.planKey) {
        warnings.add(
          DependencyWarning(
            kind: DependencyWarningKind.selfDependency,
            planKey: node.planKey,
          ),
        );
        continue;
      }
      if (!seenDeps.add(dep)) {
        warnings.add(
          DependencyWarning(
            kind: DependencyWarningKind.duplicateDependency,
            planKey: node.planKey,
            relatedPlanKey: dep,
          ),
        );
        continue;
      }
      if (!planKeys.contains(dep)) {
        warnings.add(
          DependencyWarning(
            kind: DependencyWarningKind.unknownDependency,
            planKey: node.planKey,
            relatedPlanKey: dep,
          ),
        );
      }
    }
  }

  warnings.addAll(_detectCycles(plan));
  return List<DependencyWarning>.unmodifiable(warnings);
}

/// Depth-first search over "depends on" edges, restricted to plan keys that
/// actually exist — an unknown edge is already reported separately and must
/// not also be walked into.
///
/// Iterative, with an explicit stack: a long dependency chain from a
/// malformed or very large plan must not be able to overflow the call stack.
List<DependencyWarning> _detectCycles(ProjectPlan plan) {
  final byPlanKey = <String, WorkItemNode>{
    for (final node in plan.nodes) node.planKey: node,
  };
  final visited = <String>{};
  final onStack = <String>{};
  final warnings = <DependencyWarning>[];

  for (final start in byPlanKey.keys) {
    if (!visited.add(start)) continue;
    onStack.add(start);
    final stack = <_DfsFrame>[_DfsFrame(start)];

    while (stack.isNotEmpty) {
      final frame = stack.last;
      final deps = byPlanKey[frame.planKey]!.dependsOnPlanKeys;
      if (frame.next >= deps.length) {
        onStack.remove(frame.planKey);
        stack.removeLast();
        continue;
      }
      final dep = deps[frame.next++];
      if (!byPlanKey.containsKey(dep)) continue;
      if (onStack.contains(dep)) {
        warnings.add(
          DependencyWarning(kind: DependencyWarningKind.cycle, planKey: dep),
        );
        continue;
      }
      if (!visited.add(dep)) continue;
      onStack.add(dep);
      stack.add(_DfsFrame(dep));
    }
  }
  return warnings;
}

class _DfsFrame {
  _DfsFrame(this.planKey);

  final String planKey;
  int next = 0;
}

/// One row of the rendered dependency tree.
class DependencyTreeRow {
  const DependencyTreeRow({
    required this.node,
    required this.depth,
    required this.isCycle,
    required this.isRepeat,
  });

  final WorkItemNode node;

  /// Distance from the row's starting node.
  final int depth;

  /// The node is already on the path to this row: drawn once, marked, and
  /// not walked into again.
  final bool isCycle;

  /// The node was already expanded under another parent. It is drawn here
  /// too — the edge is real — but its subtree is not repeated.
  final bool isRepeat;
}

/// The plan as tree rows, in display order.
///
/// Roots are the items with no valid dependency; each row's children are the
/// items that depend on it. Every node's subtree is expanded **once**: a node
/// several items depend on appears under each of them, but its own dependents
/// are listed only the first time. Without that, a converging plan (two
/// branches meeting, repeatedly) would render an exponential number of rows.
///
/// A node reachable from no root — every member of a pure cycle — is still
/// shown, as a starting row of its own, rather than silently disappearing.
///
/// Iterative, O(nodes + edges): no recursion, whatever the plan's depth.
List<DependencyTreeRow> flattenDependencyTree(ProjectPlan plan) {
  final planKeys = {for (final node in plan.nodes) node.planKey};
  final children = <String, List<WorkItemNode>>{};
  for (final node in plan.nodes) {
    for (final dep in node.dependsOnPlanKeys.toSet()) {
      if (planKeys.contains(dep)) {
        (children[dep] ??= <WorkItemNode>[]).add(node);
      }
    }
  }

  final roots = [
    for (final node in plan.nodes)
      if (node.dependsOnPlanKeys.every(
        (dep) => dep == node.planKey || !planKeys.contains(dep),
      ))
        node,
  ];

  final rows = <DependencyTreeRow>[];
  final expanded = <String>{};
  final onPath = <String>{};

  void walkFrom(WorkItemNode start) {
    // (node, depth); a null node marks leaving the node below it.
    final stack = <(WorkItemNode?, int, String)>[(start, 0, '')];
    while (stack.isNotEmpty) {
      final (node, depth, leaving) = stack.removeLast();
      if (node == null) {
        onPath.remove(leaving);
        continue;
      }
      final key = node.planKey;
      final isCycle = onPath.contains(key);
      final isRepeat = !isCycle && expanded.contains(key);
      rows.add(
        DependencyTreeRow(
          node: node,
          depth: depth,
          isCycle: isCycle,
          isRepeat: isRepeat,
        ),
      );
      if (isCycle || isRepeat) continue;

      expanded.add(key);
      onPath.add(key);
      stack.add((null, depth, key));
      final dependents = children[key] ?? const <WorkItemNode>[];
      for (final child in dependents.reversed) {
        stack.add((child, depth + 1, ''));
      }
    }
  }

  for (final root in roots) {
    if (!expanded.contains(root.planKey)) walkFrom(root);
  }
  for (final node in plan.nodes) {
    if (!expanded.contains(node.planKey)) walkFrom(node);
  }
  return List<DependencyTreeRow>.unmodifiable(rows);
}
