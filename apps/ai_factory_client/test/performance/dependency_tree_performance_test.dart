import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/presentation/models/dependency_graph_validation.dart';
import 'package:ai_factory_client/presentation/widgets/work_item_dependency_tree.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// Dependency trees of 50, 100 and 500 nodes in every shape the plan data
/// can take — including malformed and cyclic input — and one far deeper
/// chain to prove nothing recurses on the call stack.
///
/// The tree expands every node's subtree once. Without that rule a
/// converging plan (each layer depending on both nodes of the layer before)
/// renders 2^depth rows; with it, rows are bounded by nodes + edges.
void main() {
  setUpAll(initializeTestLocalization);

  WorkItemNode node(String key, List<String> deps) => testWorkItemNode(
    workItem: testWorkItem(id: 'w-$key', title: 'Task $key'),
    planKey: key,
    dependsOnPlanKeys: deps,
  );

  ProjectPlan plan(List<WorkItemNode> nodes) =>
      ProjectPlan(projectId: 'p1', planHash: 'h', nodes: nodes);

  final shapes = <String, ProjectPlan Function(int n)>{
    'linear': (n) => plan([
      for (var i = 0; i < n; i++) node('T$i', i == 0 ? [] : ['T${i - 1}']),
    ]),
    'branching': (n) => plan([
      for (var i = 0; i < n; i++)
        node('T$i', i == 0 ? [] : ['T${(i - 1) ~/ 2}']),
    ]),
    // Layers of two; each node depends on both nodes of the previous layer.
    'converging': (n) => plan([
      for (var i = 0; i < n; i++)
        node(
          'T$i',
          i < 2 ? [] : ['T${(i ~/ 2 - 1) * 2}', 'T${(i ~/ 2 - 1) * 2 + 1}'],
        ),
    ]),
    // A few hub nodes almost everything depends on.
    'shared': (n) => plan([
      for (var i = 0; i < n; i++)
        node('T$i', i < 3 ? [] : ['T0', 'T1', 'T2', 'T${i - 1}']),
    ]),
    // Every kind of damage at once: one cycle through every node, self
    // edges, unknown targets, duplicate edges and a duplicate plan key.
    'malformed': (n) => plan([
      for (var i = 0; i < n; i++)
        node('T$i', [
          'T${(i + 1) % n}',
          if (i.isEven) 'T$i',
          if (i % 3 == 0) 'MISSING$i',
          if (i % 5 == 0) 'T${(i + 1) % n}',
        ]),
      node('T0', const []),
    ]),
  };

  for (final shape in shapes.entries) {
    for (final n in const [50, 100, 500]) {
      test('${shape.key}, $n nodes: bounded rows, every node shown', () {
        final p = shape.value(n);
        final watch = Stopwatch()..start();
        final warnings = validateDependencyGraph(p);
        final rows = flattenDependencyTree(p);
        final ms = watch.elapsedMilliseconds;

        final edges = p.nodes.fold<int>(
          0,
          (sum, n) => sum + n.dependsOnPlanKeys.length,
        );
        expect(rows.length, lessThanOrEqualTo(p.nodes.length + edges));
        final shown = {for (final r in rows) r.node.planKey};
        expect(shown, containsAll({for (final n in p.nodes) n.planKey}));
        if (shape.key == 'malformed') {
          expect(rows.any((r) => r.isCycle), isTrue);
          expect(warnings, isNotEmpty);
        }
        expect(ms, lessThan(1000), reason: '$ms ms');
      });
    }
  }

  test('a 20 000-node chain is flattened and validated without recursion', () {
    final p = shapes['linear']!(20000);
    final cyclic = plan([
      ...p.nodes.skip(1),
      node('T0', ['T19999']),
    ]);
    expect(flattenDependencyTree(p), hasLength(20000));
    expect(validateDependencyGraph(p), isEmpty);
    expect(
      validateDependencyGraph(cyclic).map((w) => w.kind),
      contains(DependencyWarningKind.cycle),
    );
    expect(flattenDependencyTree(cyclic).length, greaterThan(19999));
  });

  group('rendering', () {
    for (final shape in const ['linear', 'converging', 'malformed']) {
      testWidgets('$shape, 500 nodes renders at phone width', (tester) async {
        final p = shapes[shape]!(500);
        final watch = Stopwatch()..start();
        await pumpScreen(
          tester,
          size: const Size(400, 900),
          home: Scaffold(
            body: SingleChildScrollView(
              child: WorkItemDependencyTree(
                plan: p,
                warnings: validateDependencyGraph(p),
              ),
            ),
          ),
        );
        final ms = watch.elapsedMilliseconds;
        // The deepest row is still readable: indentation is capped at a
        // third of the width.
        final deepest = find.text('Task T499');
        expect(deepest, findsWidgets);
        final rect = tester.getRect(deepest.first);
        expect(rect.left, lessThan(400 / 2), reason: 'indent ${rect.left}');
        expect(rect.width, greaterThan(0));
        expect(ms, lessThan(15000), reason: '$ms ms');
      });
    }
  });
}
