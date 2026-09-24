import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// What a screen reader announces for the application's important state.
///
/// Every label must stand on its own: the operator hears one node at a time,
/// without the heading above it or the colour around it.
void main() {
  setUpAll(initializeTestLocalization);

  late SemanticsHandle semantics;

  setUp(() async {
    pinCatalogClock();
    semantics = SemanticsBinding.instance.ensureSemantics();
    await registerTestServices();
  });

  tearDown(() => semantics.dispose());

  Future<void> open(WidgetTester tester, CatalogScreen screen) async {
    registerCatalog(screen);
    await pumpCatalogScreen(tester, screen, size: const Size(1280, 3000));
  }

  Finder label(Pattern pattern) => find.bySemanticsLabel(pattern);

  testWidgets('project lifecycle: the current stage and its position', (
    tester,
  ) async {
    await open(tester, CatalogScreen.projectDetail);

    expect(label(RegExp(r'^Current state: Implementation')), findsOneWidget);
    expect(
      label(RegExp(r'^Step 6 of 10: Implementation\. Current stage$')),
      findsOneWidget,
    );
    expect(
      label(RegExp(r'^Step 1 of 10: Created\. Before the current stage$')),
      findsOneWidget,
    );
  });

  testWidgets('work item: role, id, title, status, readiness, attempts', (
    tester,
  ) async {
    await open(tester, CatalogScreen.projectDetail);

    expect(
      label(
        'Work item T03, Implement the account endpoints, Status: Running, '
        'Readiness: Running, 1 attempt',
      ),
      findsOneWidget,
    );
    // The dependency row names its edges and its status.
    expect(
      label('T05 Review the implementation depends on T03, T04, Draft'),
      findsWidgets,
    );
  });

  testWidgets('agent run: role, agent, status, attempt, work item', (
    tester,
  ) async {
    await open(tester, CatalogScreen.agentRuns);

    expect(
      label(
        'Agent run: Implementation Agent, Status: Running, Attempt 1, '
        'Work item: Implement the account endpoints',
      ),
      findsOneWidget,
    );
    expect(
      label(
        'Agent run: Architecture Agent, Status: Failed, Attempt 1, '
        'Work item: Design the database schema',
      ),
      findsOneWidget,
    );
  });

  testWidgets('approval: the button states its consequence', (tester) async {
    await open(tester, CatalogScreen.humanReview);

    final approve = label(
      'Approve project — this will transition the project to Completed',
    );
    expect(approve, findsOneWidget);
    final data = tester.getSemantics(approve).getSemanticsData();
    expect(data.flagsCollection.isButton, isTrue);
    expect(data.hasAction(SemanticsAction.tap), isTrue);
  });

  testWidgets('live status: a live region with a readable state', (
    tester,
  ) async {
    await open(tester, CatalogScreen.dashboard);

    final status = label('Live updates on');
    expect(status, findsOneWidget);
    expect(
      tester
          .getSemantics(status)
          .getSemanticsData()
          .flagsCollection
          .isLiveRegion,
      isTrue,
    );
  });

  testWidgets('diagnostics: counts carry the group they belong to', (
    tester,
  ) async {
    await open(tester, CatalogScreen.diagnostics);

    // Merged into the card's node, as a screen reader reads the card.
    expect(label(RegExp('Agent runs, Active: 1')), findsOneWidget);
    expect(label(RegExp('Agent runs, Stale: 0')), findsOneWidget);
  });

  testWidgets('health: each probe is announced with its result', (
    tester,
  ) async {
    await open(tester, CatalogScreen.operations);

    expect(label(RegExp('Liveness: Healthy')), findsOneWidget);
    expect(label(RegExp('Readiness: Healthy')), findsOneWidget);
    expect(label(RegExp('Scheduler: Enabled')), findsOneWidget);
  });

  testWidgets('errors: a failed load says what failed and offers Retry', (
    tester,
  ) async {
    Get.put<ProjectRepository>(
      FakeProjectRepository()..listError = const NetworkError(),
    );
    await open(tester, CatalogScreen.projects);

    expect(label(RegExp('No connection to the AI Factory API')), findsWidgets);
    final retry = find.bySemanticsLabel('Retry');
    expect(retry, findsWidgets);
    expect(
      tester
          .getSemantics(retry.first)
          .getSemanticsData()
          .flagsCollection
          .isButton,
      isTrue,
    );
  });

  testWidgets('no status is carried by colour alone', (tester) async {
    await pumpScreen(tester, home: const _AllChips());

    // Every chip of every status enum renders a word and an icon, and is
    // announced as "<field>: <state>".
    final chips = _AllChips.chips();
    for (final chip in chips) {
      expect(chip.label.trim(), isNotEmpty);
      expect(
        label('${chip.semanticPrefix}: ${chip.label}'),
        findsWidgets,
        reason: chip.label,
      );
    }
    expect(
      find.byType(Icon).evaluate().length,
      greaterThanOrEqualTo(chips.length),
    );
  });
}

class _AllChips extends StatelessWidget {
  const _AllChips();

  static List<StatusChip> chips() => <StatusChip>[
    for (final s in ProjectStatus.values) StatusChip.project(s),
    for (final s in WorkItemStatus.values) StatusChip.workItem(s),
    for (final s in AgentRunStatus.values) StatusChip.agentRun(s),
    for (final s in TaskReadiness.values) StatusChip.readiness(s),
  ];

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SingleChildScrollView(child: Wrap(children: chips())),
  );
}
