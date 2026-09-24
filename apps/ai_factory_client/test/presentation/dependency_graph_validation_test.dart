import 'package:ai_factory_client/domain/models/work_item.dart';
import 'package:ai_factory_client/presentation/models/dependency_graph_validation.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';

/// Workstream Y: the pure dependency projection must survive every malformed
/// input the two-endpoint join (`G-DEPS`) can produce, and it must never
/// throw or loop forever.
void main() {
  ProjectPlan plan(List<WorkItemNode> nodes) =>
      ProjectPlan(projectId: 'p1', planHash: 'hash', nodes: nodes);

  group('valid graphs produce no warnings', () {
    test('linear chain', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01']),
        testWorkItemNode(planKey: 'T03', dependsOnPlanKeys: ['T02']),
      ]);
      expect(validateDependencyGraph(graph), isEmpty);
    });

    test('branching', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01']),
        testWorkItemNode(planKey: 'T03', dependsOnPlanKeys: ['T01']),
      ]);
      expect(validateDependencyGraph(graph), isEmpty);
    });

    test('converging dependencies', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02'),
        testWorkItemNode(planKey: 'T03', dependsOnPlanKeys: ['T01', 'T02']),
      ]);
      expect(validateDependencyGraph(graph), isEmpty);
    });

    test('independent work items', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02'),
      ]);
      expect(validateDependencyGraph(graph), isEmpty);
    });

    test('empty graph', () {
      expect(validateDependencyGraph(plan(const [])), isEmpty);
    });
  });

  group('malformed graphs are reported, never thrown', () {
    test('self dependency', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01', dependsOnPlanKeys: ['T01']),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.selfDependency),
        isTrue,
      );
    });

    test('duplicate dependency', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01', 'T01']),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any(
          (w) => w.kind == DependencyWarningKind.duplicateDependency,
        ),
        isTrue,
      );
    });

    test('unknown dependency target', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01', dependsOnPlanKeys: ['T99']),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any(
          (w) =>
              w.kind == DependencyWarningKind.unknownDependency &&
              w.relatedPlanKey == 'T99',
        ),
        isTrue,
      );
    });

    test('duplicate plan key', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(
          planKey: 'T01',
          workItem: testWorkItem(id: 'other'),
        ),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.duplicateWorkItem),
        isTrue,
      );
    });

    test('duplicate work item id under different plan keys', () {
      final shared = testWorkItem(id: 'same-id');
      final graph = plan([
        testWorkItemNode(planKey: 'T01', workItem: shared),
        testWorkItemNode(planKey: 'T02', workItem: shared),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.duplicateWorkItem),
        isTrue,
      );
    });

    test('direct cycle is detected without recursing forever', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01', dependsOnPlanKeys: ['T02']),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01']),
      ]);
      // A synchronous call returning at all proves the DFS terminated; an
      // undetected cycle here would blow the call stack instead of hanging.
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.cycle),
        isTrue,
      );
    });

    test('longer cycle is detected', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01', dependsOnPlanKeys: ['T03']),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01']),
        testWorkItemNode(planKey: 'T03', dependsOnPlanKeys: ['T02']),
      ]);
      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.cycle),
        isTrue,
      );
    });

    test('the valid portion of a malformed graph still resolves', () {
      final graph = plan([
        testWorkItemNode(planKey: 'T01'),
        testWorkItemNode(planKey: 'T02', dependsOnPlanKeys: ['T01']),
        // T03 has a self-dependency and an unknown target only, so it has no
        // *valid* dependency — `ProjectPlan.isRoot` still reports false for
        // it (its list is non-empty), which is exactly why the tree widget
        // computes its own effective-roots set rather than trusting `.roots`
        // for a malformed plan; see `WorkItemDependencyTree._effectiveRoots`.
        testWorkItemNode(planKey: 'T03', dependsOnPlanKeys: ['T03', 'T99']),
      ]);
      expect(graph.roots.map((n) => n.planKey), ['T01']);
      expect(graph.childrenOf('T01').map((n) => n.planKey), ['T02']);

      final warnings = validateDependencyGraph(graph);
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.selfDependency),
        isTrue,
      );
      expect(
        warnings.any((w) => w.kind == DependencyWarningKind.unknownDependency),
        isTrue,
      );
    });
  });
}
