import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/agent_runs_controller.dart';
import 'package:ai_factory_client/presentation/controllers/work_items_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// Keyboard-only operation on Windows desktop: Tab, Shift+Tab, Enter, Space,
/// Escape and the arrow keys — Material's own behaviour, no custom key
/// handling except Escape in the two dialogs.
///
/// Everything runs at a real 1280×900 window, so a control below the fold
/// must be reached the way an operator reaches it: focus traversal scrolls
/// the page to it.
void main() {
  setUpAll(initializeTestLocalization);

  const window = Size(1280, 900);

  late SemanticsHandle semantics;

  setUp(() async {
    pinCatalogClock();
    semantics = SemanticsBinding.instance.ensureSemantics();
    await registerTestServices();
  });

  tearDown(() => semantics.dispose());

  group('critical flow, keyboard only', () {
    testWidgets('Dashboard → Projects → Project Detail → WorkItem → AgentRun '
        '→ back → Operations', (tester) async {
      registerCatalog(CatalogScreen.projectDetail);
      await pumpCatalogScreen(tester, CatalogScreen.dashboard, size: window);

      await tabTo(tester, 'Projects');
      await press(tester, LogicalKeyboardKey.enter);
      expect(Get.currentRoute, AppRoutes.projects);

      await tabTo(tester, 'Customer API');
      await press(tester, LogicalKeyboardKey.enter);
      expect(
        Get.currentRoute,
        AppRoutes.projectDetail(ScreenCatalog.implementationId),
      );

      await tabTo(tester, 'Work item T03');
      await press(tester, LogicalKeyboardKey.enter);
      expect(
        Get.find<WorkItemsController>().selectedWorkItemId.value,
        'w3-${ScreenCatalog.implementationId}',
      );

      await tabTo(tester, 'Agent run: Architecture Agent, Status: Succeeded');
      await press(tester, LogicalKeyboardKey.space);
      expect(
        Get.find<AgentRunsController>().selectedAgentRunId.value,
        'r2-${ScreenCatalog.implementationId}',
      );

      await tabTo(tester, 'Back', backwards: true);
      await press(tester, LogicalKeyboardKey.enter);
      expect(Get.currentRoute, AppRoutes.projects);

      await tabTo(tester, 'Operations');
      await press(tester, LogicalKeyboardKey.enter);
      expect(Get.currentRoute, AppRoutes.operations);
    });

    testWidgets('arrow keys choose an agent-run status filter', (tester) async {
      registerCatalog(CatalogScreen.agentRuns);
      await pumpCatalogScreen(
        tester,
        CatalogScreen.projectDetail,
        size: window,
      );

      await tabTo(tester, 'All statuses');
      await press(tester, LogicalKeyboardKey.enter);
      await press(tester, LogicalKeyboardKey.arrowDown);
      await press(tester, LogicalKeyboardKey.enter);

      expect(Get.find<AgentRunsController>().statusFilter.value, isNotNull);
    });
  });

  group('Project Detail focus order', () {
    testWidgets('Back, project actions, WorkItems, dependencies, AgentRuns, '
        'Human Review', (tester) async {
      registerCatalog(CatalogScreen.humanReview);
      await pumpCatalogScreen(tester, CatalogScreen.humanReview, size: window);
      await tester.drag(find.byType(Scrollable).first, const Offset(0, 5000));
      await tester.pumpAndSettle();

      final order = <String>[];
      for (var i = 0; i < 40; i++) {
        final label = await tab(tester);
        if (order.isNotEmpty && label == order.first) break;
        order.add(label);
      }

      int first(String prefix) {
        final index = order.indexWhere((l) => l.startsWith(prefix));
        expect(index, isNonNegative, reason: '$prefix not in $order');
        return index;
      }

      final positions = [
        first('Back'),
        first('Diagnostics'),
        first('Refresh'),
        first('Work item T01'),
        first('T01 Analyze requirements'),
        first('Refresh agent runs'),
        first('All statuses'),
        first('Agent run:'),
        first('Approve project'),
      ];
      expect(positions, orderedEquals([...positions]..sort()));
    });
  });

  group('Human Review approval dialog', () {
    late FakeApprovalRepository approval;

    setUp(() {
      // Registered first: `Get.put` keeps an existing registration, so the
      // catalog's own approval fake must not get there before this one.
      approval = FakeApprovalRepository();
      Get.put<ApprovalRepository>(approval);
      registerCatalog(CatalogScreen.humanReview);
    });

    testWidgets('Approve opens the dialog on Cancel; Tab and Shift+Tab move '
        'between its two actions; Escape cancels and returns focus', (
      tester,
    ) async {
      await pumpCatalogScreen(tester, CatalogScreen.humanReview, size: window);

      await tabTo(tester, 'Approve project — this will transition');
      await press(tester, LogicalKeyboardKey.enter);
      expect(find.byType(AlertDialog), findsOneWidget);
      expect(focused(tester), 'Cancel', reason: 'the safe action is default');

      expect(await tab(tester), startsWith('Approve project — this will'));
      expect(await tab(tester, backwards: true), 'Cancel');

      await press(tester, LogicalKeyboardKey.escape);
      expect(find.byType(AlertDialog), findsNothing);
      expect(approval.approveCount, 0);
      expect(focused(tester), startsWith('Approve project — this will'));
    });

    testWidgets("Space on the dialog's Approve sends exactly one approval", (
      tester,
    ) async {
      await pumpCatalogScreen(tester, CatalogScreen.humanReview, size: window);

      await tabTo(tester, 'Approve project — this will transition');
      await press(tester, LogicalKeyboardKey.enter);
      await tab(tester);
      await press(tester, LogicalKeyboardKey.space);

      expect(find.byType(AlertDialog), findsNothing);
      expect(approval.approveCount, 1);
    });
  });

  group('Create Project dialog', () {
    testWidgets('opens from the keyboard and Escape closes it', (tester) async {
      registerCatalog(CatalogScreen.projects);
      await pumpCatalogScreen(tester, CatalogScreen.projects, size: window);

      await tabTo(tester, 'New project');
      await press(tester, LogicalKeyboardKey.enter);
      expect(find.byType(AlertDialog), findsOneWidget);

      await press(tester, LogicalKeyboardKey.escape);
      expect(find.byType(AlertDialog), findsNothing);
    });
  });
}

/// The label a screen reader announces for the focused control.
String focused(WidgetTester tester) {
  final context = FocusManager.instance.primaryFocus?.context;
  if (context == null) return '<nothing focused>';
  final node = tester.getSemantics(
    find.byElementPredicate((element) => identical(element, context)),
  );
  return [node.label, node.tooltip].where((s) => s.isNotEmpty).join(' ');
}

Future<void> press(WidgetTester tester, LogicalKeyboardKey key) async {
  await tester.sendKeyEvent(key);
  await tester.pumpAndSettle();
}

/// Moves focus one step and returns the newly focused control's label.
Future<String> tab(WidgetTester tester, {bool backwards = false}) async {
  if (backwards) await tester.sendKeyDownEvent(LogicalKeyboardKey.shiftLeft);
  await tester.sendKeyEvent(LogicalKeyboardKey.tab);
  if (backwards) await tester.sendKeyUpEvent(LogicalKeyboardKey.shiftLeft);
  await tester.pumpAndSettle();
  return focused(tester);
}

/// Tabs until the focused control's label starts with [label].
Future<void> tabTo(
  WidgetTester tester,
  String label, {
  bool backwards = false,
}) async {
  final visited = <String>[];
  for (var i = 0; i < 80; i++) {
    final current = await tab(tester, backwards: backwards);
    if (current.startsWith(label)) return;
    visited.add(current);
  }
  fail('"$label" is not reachable by keyboard; focus visited: $visited');
}
