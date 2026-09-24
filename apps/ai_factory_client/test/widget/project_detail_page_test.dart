import 'dart:async';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/widgets/project_lifecycle_widget.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

/// The Project Detail screen, rendered through the real route and the real
/// translations.
///
/// The assertions are deliberately about *text an operator would read*: a
/// screen that renders the right widgets with the wrong words is still wrong,
/// and this phase's whole risk is a screen that says more than the API does.
void main() {
  setUpAll(initializeTestLocalization);

  const projectId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

  late FakeProjectRepository repository;
  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
    repository = FakeProjectRepository();
    Get.put<ProjectRepository>(repository);
    Get.put<WorkItemRepository>(FakeWorkItemRepository());
    Get.put<AgentRunRepository>(FakeAgentRunRepository());
    // Phase 19.7 — ApprovalRepository is required by HumanReviewController
    // which is now registered in the projectDetailPattern route binding.
    Get.put<ApprovalRepository>(FakeApprovalRepository());
  });

  /// Stages one project and opens its detail route.
  Future<void> open(
    WidgetTester tester, {
    Project? project,
    Size size = TestSizes.expanded,
    Locale? startLocale,
    String id = projectId,
  }) async {
    if (project != null) repository.projects = <Project>[project];
    await pumpScreen(
      tester,
      initialRoute: AppRoutes.projectDetail(id),
      getPages: AppRoutes.pages,
      size: size,
      startLocale: startLocale,
    );
  }

  Project project({
    ProjectStatus status = ProjectStatus.implementation,
    String name = 'Customer API',
    String? description = 'A customer-facing API.',
    bool started = true,
    String? currentError,
    String? gitRepository,
    String? gitBranch,
    String? rawStatus,
  }) => testProject(
    id: projectId,
    name: name,
    description: description,
    status: status,
    rawStatus: rawStatus,
    currentError: currentError,
    gitRepository: gitRepository,
    gitBranch: gitBranch,
    lifecycleStartedAtUtc: started ? DateTime.utc(2026, 9, 19, 12) : null,
  );

  group('screen states', () {
    testWidgets('the first read shows a loading state, not a blank page', (
      tester,
    ) async {
      final gate = Completer<void>();
      repository
        ..projects = <Project>[project()]
        ..gate = gate.future;

      Get.testMode = true;
      await setSurfaceSize(tester, TestSizes.expanded);
      await tester.pumpWidget(
        TestAppScope(
          initialRoute: AppRoutes.projectDetail(projectId),
          getPages: AppRoutes.pages,
        ),
      );
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsWidgets);

      gate.complete();
      await tester.pumpAndSettle();
      expect(find.text('Customer API'), findsWidgets);
    });

    testWidgets('a loaded project shows identity, lifecycle and source', (
      tester,
    ) async {
      await open(
        tester,
        project: project(
          gitRepository: 'https://github.com/acme/api.git',
          gitBranch: 'main',
        ),
      );

      // Fetched by id, so the screen works as a deep link.
      expect(repository.calls, contains('getById:$projectId'));

      expect(find.text('Customer API'), findsWidgets);
      expect(find.text('Lifecycle'), findsOneWidget);
      expect(find.byType(ProjectLifecycleWidget), findsOneWidget);
      expect(find.text('Overview'), findsOneWidget);
      expect(find.text('A customer-facing API.'), findsOneWidget);
      expect(find.text('Source'), findsOneWidget);
      expect(find.text('Git repository'), findsOneWidget);
      expect(find.text('https://github.com/acme/api.git'), findsOneWidget);
      expect(find.text('main'), findsOneWidget);
      expect(find.text(projectId), findsOneWidget);

      expectNoUntranslatedKeys(tester);
    });

    testWidgets('a failed read offers a retry and then recovers', (
      tester,
    ) async {
      repository
        ..getByIdError = const NetworkError()
        ..failOnce = true
        ..projects = <Project>[project(name: 'Recovered')];

      await open(tester);
      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.textContaining('No connection'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      expect(find.text('Recovered'), findsWidgets);
    });

    testWidgets('404 is a not-found state with a route back, not a retry', (
      tester,
    ) async {
      await open(tester, id: 'does-not-exist');

      expect(find.text('Project not found'), findsOneWidget);
      expect(
        find.textContaining('No project with this identifier exists'),
        findsOneWidget,
      );
      // Retrying a 404 returns a 404, so it is not offered.
      expect(find.widgetWithText(FilledButton, 'Retry'), findsNothing);
      expect(find.text('Back to projects'), findsOneWidget);
      expectNoUntranslatedKeys(tester);
    });

    testWidgets('a refresh keeps the project visible', (tester) async {
      await open(tester, project: project());
      expect(find.text('Customer API'), findsWidgets);

      final gate = Completer<void>();
      repository.gate = gate.future;

      await tester.tap(find.byTooltip('Refresh'));
      await tester.pump();

      // Refreshing: a thin line, and the project still on screen.
      expect(find.byType(LinearProgressIndicator), findsOneWidget);
      expect(find.text('Customer API'), findsWidgets);

      gate.complete();
      await tester.pumpAndSettle();
      expect(repository.getByIdCount, 2);
    });
  });

  group('lifecycle presentation', () {
    testWidgets('the current stage is named in words, not only in colour', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.implementation),
      );

      expect(find.text('Current state'), findsOneWidget);
      // Once as the headline, once as the header's status chip, once on the
      // track node.
      expect(find.text('Implementation'), findsWidgets);
      expect(find.text('Current stage'), findsOneWidget);
    });

    testWidgets('every backend stage is on the track', (tester) async {
      await open(tester, project: project(status: ProjectStatus.planning));

      for (final label in <String>[
        'Created',
        'Analyzing',
        'Specification',
        'Architecture',
        'Planning',
        'Implementation',
        'Testing',
        'Reviewing',
        'Human review',
        'Completed',
      ]) {
        expect(find.text(label), findsWidgets, reason: label);
      }
    });

    testWidgets('it states that stage history is not exposed', (tester) async {
      await open(tester, project: project());

      expect(find.text('Stage history'), findsOneWidget);
      expect(
        find.textContaining('The API reports the current status only'),
        findsOneWidget,
      );
    });

    testWidgets('Completed is a successful terminal state with no approval', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.completed));

      expect(find.text('Completed'), findsWidgets);
      expect(
        find.textContaining('reachable only through human approval'),
        findsOneWidget,
      );
      // Approval is Phase 19.7 and the endpoint is not called here.
      expect(find.widgetWithText(FilledButton, 'Approve'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    });

    testWidgets('Failed marks no stage and says the stage is not exposed', (
      tester,
    ) async {
      await open(
        tester,
        project: project(
          status: ProjectStatus.failed,
          currentError: 'Planner produced no work items.',
        ),
      );

      expect(find.text('Failed'), findsWidgets);
      expect(
        find.textContaining('The stage it stopped at is not exposed'),
        findsOneWidget,
      );
      // No stage may claim to be the current one.
      expect(find.text('Current stage'), findsNothing);
      // The backend's own error text, verbatim.
      expect(find.text('Error recorded by the backend'), findsOneWidget);
      expect(find.text('Planner produced no work items.'), findsOneWidget);
      // No invented retry: the backend has no transition out of Failed.
      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    });

    testWidgets('Cancelled is stated plainly and offers no Start', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.cancelled));

      expect(find.text('Cancelled'), findsWidgets);
      expect(find.textContaining('The project was cancelled'), findsOneWidget);
      expect(find.text('Current stage'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    });

    testWidgets('Paused is stated without inventing a resume action', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.paused));

      expect(find.text('Paused'), findsWidgets);
      expect(
        find.textContaining('The stage it was paused in is not exposed'),
        findsOneWidget,
      );
      // The API exposes no resume target, so no resume button exists.
      expect(find.widgetWithText(FilledButton, 'Resume'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    });

    testWidgets('Repairing anchors on Testing rather than claiming a stage', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.repairing));

      expect(find.text('Repairing'), findsWidgets);
      expect(find.text('The repair loop returns here'), findsOneWidget);
      expect(find.text('Current stage'), findsNothing);
    });

    testWidgets('a state a newer backend introduced stays visible', (
      tester,
    ) async {
      await open(
        tester,
        project: project(
          status: ProjectStatus.unknown,
          rawStatus: 'Rehearsing',
        ),
      );

      expect(find.textContaining('Rehearsing'), findsWidgets);
      expect(
        find.textContaining('a state this client does not recognize'),
        findsOneWidget,
      );
    });
  });

  group('actions', () {
    testWidgets('Start is offered only in the state the backend accepts', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.analyzing, started: false),
      );
      expect(find.widgetWithText(FilledButton, 'Start'), findsOneWidget);
    });

    testWidgets('a project in Created is told why it cannot start yet', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.created, started: false),
      );

      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
      expect(
        find.textContaining('The project source has not been initialized'),
        findsOneWidget,
      );
    });

    testWidgets('Start shows the status the server returned', (tester) async {
      await open(
        tester,
        project: project(status: ProjectStatus.analyzing, started: false),
      );

      repository.startResult = testProject(
        id: projectId,
        name: 'Customer API',
        status: ProjectStatus.planning,
        lifecycleStartedAtUtc: DateTime.utc(2026, 9, 19, 12),
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Start'));
      await tester.pumpAndSettle();

      expect(repository.startCount, 1);
      expect(find.text('Planning'), findsWidgets);
      expect(find.widgetWithText(FilledButton, 'Start'), findsNothing);
    });

    testWidgets('a rejected Start is a banner, not a blanked screen', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.analyzing, started: false),
      );

      repository
        ..mutationError = const ConflictError()
        ..failOnce = true;

      await tester.tap(find.widgetWithText(FilledButton, 'Start'));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('The resource changed in the meantime'),
        findsOneWidget,
      );
      // The project is still readable.
      expect(find.text('Customer API'), findsWidgets);
    });
  });

  group('overview and source', () {
    testWidgets('an empty description says so instead of showing nothing', (
      tester,
    ) async {
      await open(tester, project: project(description: null));

      expect(find.text('This project has no description.'), findsOneWidget);
    });

    testWidgets('a multi-line description is shown in full', (tester) async {
      await open(
        tester,
        project: project(
          description:
              'Line one.\nLine two.\nLine three is a good deal '
              'longer so that it has to wrap at least once on a narrow '
              'window, which is exactly what an operator will paste in.',
        ),
      );

      expect(
        find.textContaining('Line three is a good deal longer'),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('an empty workspace is distinguished from a git source', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.analyzing));

      expect(find.text('Empty workspace'), findsWidgets);
      expect(find.text('Repository'), findsNothing);
    });

    testWidgets('credentials never reach the widget tree', (tester) async {
      await open(
        tester,
        project: project(
          gitRepository: 'https://x-access-token:ghp_secret@github.com/a/b.git',
        ),
      );

      expect(find.textContaining('ghp_secret'), findsNothing);
      expect(find.text('https://github.com/a/b.git'), findsOneWidget);
    });
  });

  group('localization', () {
    testWidgets('the whole screen renders in German', (tester) async {
      await open(
        tester,
        project: project(
          status: ProjectStatus.humanReview,
          gitRepository: 'https://github.com/acme/api.git',
        ),
        startLocale: const Locale('de'),
      );

      expect(find.text('Lebenszyklus'), findsOneWidget);
      expect(find.text('Aktueller Zustand'), findsOneWidget);
      expect(find.text('Menschliche Freigabe'), findsWidgets);
      expect(find.text('Aktuelle Phase'), findsOneWidget);
      expect(find.text('Übersicht'), findsOneWidget);
      expect(find.text('Quelle'), findsOneWidget);
      expect(find.text('Git-Repository'), findsOneWidget);
      expect(find.text('Kennung'), findsOneWidget);

      // Operator data is never translated.
      expect(find.text('Customer API'), findsWidgets);
      expect(find.text('A customer-facing API.'), findsOneWidget);
      expect(find.text('https://github.com/acme/api.git'), findsOneWidget);

      expectNoUntranslatedKeys(tester);
    });

    testWidgets('switching language at runtime relabels the lifecycle', (
      tester,
    ) async {
      await open(tester, project: project(status: ProjectStatus.reviewing));
      expect(find.text('Reviewing'), findsWidgets);
      expect(find.text('Lifecycle'), findsOneWidget);
      expect(find.text('Stage history'), findsOneWidget);

      final context = tester.element(find.byType(Scaffold).first);
      await localeService.apply(context, LocalePreference.german);
      await tester.pumpAndSettle();

      expect(find.text('Prüfung'), findsWidgets);
      expect(find.text('Reviewing'), findsNothing);
      // A pushed route rebuilds on a locale change only for widgets that
      // depend on an inherited value. These two are the section titles that
      // depend on nothing else, so they are the ones that go stale first if
      // the screen stops registering its locale dependency.
      expect(find.text('Lebenszyklus'), findsOneWidget);
      expect(find.text('Phasenverlauf'), findsOneWidget);
      expect(find.text('Lifecycle'), findsNothing);
      expect(find.text('Stage history'), findsNothing);
      expectNoUntranslatedKeys(tester);

      await localeService.apply(
        tester.element(find.byType(Scaffold).first),
        LocalePreference.english,
      );
      await tester.pumpAndSettle();
      expect(find.text('Reviewing'), findsWidgets);
      expect(find.text('Lifecycle'), findsOneWidget);
      expect(find.text('Lebenszyklus'), findsNothing);
    });
  });

  group('responsive layout', () {
    for (final entry in <String, Size>{
      'compact': TestSizes.compact,
      'medium': TestSizes.medium,
      'expanded': TestSizes.expanded,
    }.entries) {
      testWidgets('${entry.key} lays out German labels without overflow', (
        tester,
      ) async {
        await open(
          tester,
          project: project(
            status: ProjectStatus.humanReview,
            currentError: 'Der Planer hat keine Arbeitspakete erzeugt.',
            gitRepository: 'https://github.com/acme/api.git',
            gitBranch: 'feature/sehr-langer-branch-name',
          ),
          size: entry.value,
          startLocale: const Locale('de'),
        );

        expect(tester.takeException(), isNull);
        expect(find.byType(ProjectDetailPage), findsOneWidget);

        // The critical controls stay reachable at every width.
        expect(find.byTooltip('Aktualisieren'), findsOneWidget);
        expect(find.byType(ProjectLifecycleWidget), findsOneWidget);
        expect(find.text('Menschliche Freigabe'), findsWidgets);
      });
    }

    testWidgets('a long German status is not clipped on a narrow window', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.specification),
        size: const Size(360, 800),
        startLocale: const Locale('de'),
      );

      expect(tester.takeException(), isNull);
      final text = tester.widget<Text>(find.text('Spezifikation').first);
      expect(text.overflow, isNot(TextOverflow.ellipsis));
    });
  });

  group('accessibility', () {
    testWidgets('each stage announces its step, name and position', (
      tester,
    ) async {
      // Disposed inside the test body: the end-of-test verification runs
      // before tear-downs do.
      final handle = tester.ensureSemantics();
      await open(tester, project: project(status: ProjectStatus.testing));

      expect(
        find.bySemanticsLabel('Step 7 of 10: Testing. Current stage'),
        findsOneWidget,
      );
      expect(
        find.bySemanticsLabel(
          'Step 1 of 10: Created. Before the current stage',
        ),
        findsOneWidget,
      );
      expect(
        find.bySemanticsLabel(
          'Step 10 of 10: Completed. After the current stage',
        ),
        findsOneWidget,
      );

      handle.dispose();
    });

    testWidgets('icon-only controls carry a tooltip', (tester) async {
      await open(tester, project: project());
      expect(find.byTooltip('Refresh'), findsOneWidget);
    });
  });

  // ---------------------------------------------------------------------------
  // Phase 19.7 — Human Review section
  // ---------------------------------------------------------------------------

  group('Human Review section', () {
    /// Drags until [finder] is in view or gives up after 12 attempts.
    Future<void> reveal(WidgetTester tester, Finder finder) async {
      if (finder.evaluate().isNotEmpty) return;
      final list = find.byType(ListView).first;
      for (var i = 0; i < 12; i++) {
        await tester.drag(list, const Offset(0, -600));
        await tester.pumpAndSettle();
        if (finder.evaluate().isNotEmpty) return;
      }
    }

    testWidgets('Human Review section is visible when status is HumanReview', (
      tester,
    ) async {
      await open(
        tester,
        project: project(status: ProjectStatus.humanReview, started: true),
      );

      await reveal(tester, find.text('Human Review Required'));
      expect(find.text('Human Review Required'), findsOneWidget);
      expect(
        find.widgetWithText(FilledButton, 'Approve project'),
        findsOneWidget,
      );
    });

    testWidgets(
      'Human Review section is absent when status is not HumanReview',
      (tester) async {
        await open(
          tester,
          project: project(status: ProjectStatus.implementation, started: true),
        );

        // Scroll all the way down.
        final list = find.byType(ListView).first;
        for (var i = 0; i < 12; i++) {
          await tester.drag(list, const Offset(0, -600));
          await tester.pumpAndSettle();
        }

        expect(find.text('Human Review Required'), findsNothing);
        expect(
          find.widgetWithText(FilledButton, 'Approve project'),
          findsNothing,
        );
      },
    );

    testWidgets(
      'Completed status shows completed note, not the approval section',
      (tester) async {
        await open(
          tester,
          project: project(status: ProjectStatus.completed, started: true),
        );

        await reveal(tester, find.text('Project Completed'));
        expect(find.text('Project Completed'), findsOneWidget);
        expect(find.text('Human Review Required'), findsNothing);
      },
    );
  });
}
