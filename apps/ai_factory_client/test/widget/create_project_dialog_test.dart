import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/localization/locale_service.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:ai_factory_client/presentation/controllers/create_project_controller.dart';
import 'package:ai_factory_client/presentation/dialogs/create_project_dialog.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/fake_repositories.dart';
import '../support/test_app.dart';

void main() {
  setUpAll(initializeTestLocalization);

  late FakeProjectRepository projects;
  late CreateProjectController controller;
  late LocaleService localeService;

  setUp(() async {
    localeService = await registerTestServices();
    projects = FakeProjectRepository();
    controller = CreateProjectController(projects: projects);
  });

  tearDown(() => controller.onClose());

  /// Opens the dialog over a trivial host screen and hands back the result
  /// future, so a test can assert what the flow returned to Projects.
  Future<Project?> open(
    WidgetTester tester, {
    Size size = TestSizes.expanded,
  }) async {
    Project? result;
    await pumpScreen(
      tester,
      size: size,
      home: Builder(
        builder: (context) => Scaffold(
          body: Center(
            child: FilledButton(
              onPressed: () async {
                result = await showCreateProjectDialog(
                  context,
                  controller: controller,
                );
              },
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return result;
  }

  Future<void> fillDetails(
    WidgetTester tester, {
    String name = 'Customer API',
    String description = 'Build a customer-facing API.',
  }) async {
    await tester.enterText(find.byType(TextFormField).at(0), name);
    await tester.enterText(find.byType(TextFormField).at(1), description);
    await tester.pumpAndSettle();
  }

  testWidgets('the form shows both required fields', (tester) async {
    await open(tester);

    expect(find.text('New project'), findsWidgets);
    expect(find.byType(TextFormField), findsNWidgets(2));
    expect(find.widgetWithText(FilledButton, 'Create'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });

  testWidgets('validation messages are localized and block submission', (
    tester,
  ) async {
    await open(tester);

    await tester.enterText(find.byType(TextFormField).at(0), 'bad/name');
    await tester.pumpAndSettle();

    expect(
      find.textContaining('cannot contain'),
      findsOneWidget,
      reason: 'the invalid-character rule must be stated, not just refused',
    );

    // Pressing Create on an untouched field states what is missing rather
    // than silently doing nothing.
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(find.textContaining('A description is required'), findsOneWidget);
    expect(projects.createRequests, isEmpty);
  });

  testWidgets('a valid form creates the project and moves to the source step', (
    tester,
  ) async {
    await open(tester);
    await fillDetails(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(projects.createRequests, hasLength(1));
    expect(find.text('Source'), findsOneWidget);
    expect(find.text('Empty workspace'), findsOneWidget);
    expect(find.text('Git repository'), findsOneWidget);
    expect(
      find.widgetWithText(FilledButton, 'Initialize source'),
      findsOneWidget,
    );
  });

  testWidgets('completing both steps returns the created project', (
    tester,
  ) async {
    Project? returned;
    await pumpScreen(
      tester,
      home: Builder(
        builder: (context) => Scaffold(
          body: Center(
            child: FilledButton(
              onPressed: () async {
                returned = await showCreateProjectDialog(
                  context,
                  controller: controller,
                );
              },
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    await fillDetails(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Initialize source'));
    await tester.pumpAndSettle();

    expect(
      find.text('Project created and its source initialized.'),
      findsOneWidget,
    );
    // The status shown is the one the server returned.
    expect(find.text('Analyzing'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Close'));
    await tester.pumpAndSettle();

    expect(returned, isNotNull);
    expect(returned!.name, 'Customer API');
  });

  testWidgets('the Create button is disabled while the request is pending', (
    tester,
  ) async {
    await open(tester);
    await fillDetails(tester);

    final gate = Completer<void>();
    projects.gate = gate.future;

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pump();

    // The label is replaced by a spinner and the button no longer fires, so a
    // double-click cannot produce two projects.
    final button = tester.widget<FilledButton>(find.byType(FilledButton).last);
    expect(button.onPressed, isNull);

    gate.complete();
    await tester.pumpAndSettle();
    expect(projects.createRequests, hasLength(1));
  });

  testWidgets('a server validation failure keeps the form and its values', (
    tester,
  ) async {
    projects.mutationError = const ValidationError(
      technicalDetail: 'Project name contains invalid characters.',
    );

    await open(tester);
    await fillDetails(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(find.text('The request was rejected as invalid.'), findsOneWidget);
    // The server names the offending field; that belongs on screen.
    expect(
      find.text('Project name contains invalid characters.'),
      findsWidgets,
    );
    // Nothing the operator typed was thrown away.
    expect(find.text('Customer API'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Create'), findsOneWidget);
  });

  testWidgets('a network failure is shown and the step can be retried', (
    tester,
  ) async {
    projects.mutationError = const NetworkError();
    projects.failOnce = true;

    await open(tester);
    await fillDetails(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(find.textContaining('No connection'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(find.text('Source'), findsOneWidget);
  });

  testWidgets('a Git source asks for the repository and rejects an empty one', (
    tester,
  ) async {
    await open(tester);
    await fillDetails(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Git repository'));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(TextFormField, ''), findsWidgets);
    await tester.tap(find.widgetWithText(FilledButton, 'Initialize source'));
    await tester.pumpAndSettle();

    expect(
      find.text('A repository URL is required for a Git source.'),
      findsOneWidget,
    );
    expect(
      projects.calls.where((c) => c.startsWith('initializeSource')),
      isEmpty,
    );
  });

  testWidgets('the dialog renders in German', (tester) async {
    await open(tester);

    final context = tester.element(find.byType(AlertDialog));
    await localeService.apply(context, LocalePreference.german);
    await tester.pumpAndSettle();

    expect(find.text('Neues Projekt'), findsWidgets);
    expect(find.text('Beschreibung'), findsWidgets);
    expect(find.widgetWithText(FilledButton, 'Anlegen'), findsOneWidget);
    expectNoUntranslatedKeys(tester);
  });

  for (final entry in <String, Size>{
    'compact': TestSizes.compact,
    'tablet': TestSizes.medium,
    'desktop': TestSizes.expanded,
  }.entries) {
    testWidgets('lays out without overflow at ${entry.key} width', (
      tester,
    ) async {
      await open(tester, size: entry.value);
      await fillDetails(tester);

      expect(tester.takeException(), isNull);
      expect(find.widgetWithText(FilledButton, 'Create'), findsOneWidget);
    });
  }
}
