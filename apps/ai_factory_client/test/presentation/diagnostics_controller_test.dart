import 'dart:async';

import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/operations.dart';
import 'package:ai_factory_client/presentation/controllers/diagnostics_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

void main() {
  const projectId = 'p1';

  late FakeDiagnosticsRepository diagnostics;

  DiagnosticsController build() =>
      DiagnosticsController(projectId: projectId, diagnostics: diagnostics);

  setUp(() {
    Get.testMode = true;
    diagnostics = FakeDiagnosticsRepository();
  });

  tearDown(() async => Get.deleteAll(force: true));

  test('loads the project diagnostics', () async {
    final controller = build();

    await controller.load();

    expect(controller.state.value, isA<Success<ProjectDiagnostics>>());
    expect(controller.state.value.dataOrNull?.projectId, projectId);
    expect(controller.needsOperatorAccess, isFalse);
    expect(controller.isNotFound, isFalse);
  });

  test('empty status counts load as a real, empty result', () async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(
      workItemStatusCounts: const <String, int>{},
    );
    final controller = build();

    await controller.load();

    expect(controller.state.value, isA<Success<ProjectDiagnostics>>());
    expect(controller.state.value.dataOrNull?.totalWorkItems, 0);
  });

  test('401 is an operator-access state, not a server failure', () async {
    diagnostics.projectDiagnosticsError = const AuthenticationError();
    final controller = build();

    await controller.load();

    expect(controller.needsOperatorAccess, isTrue);
    expect(controller.isNotFound, isFalse);
  });

  test('404 is its own state', () async {
    diagnostics.projectDiagnosticsError = const NotFoundError();
    final controller = build();

    await controller.load();

    expect(controller.isNotFound, isTrue);
    expect(controller.needsOperatorAccess, isFalse);
  });

  test('a network error is reported as a failure', () async {
    diagnostics.projectDiagnosticsError = const NetworkError();
    final controller = build();

    await controller.load();

    final state = controller.state.value;
    expect(state, isA<Failure<ProjectDiagnostics>>());
    expect((state as Failure<ProjectDiagnostics>).error, isA<NetworkError>());
  });

  test('refresh keeps the current diagnostics on screen', () async {
    final controller = build();
    await controller.load();

    final gate = Completer<void>();
    diagnostics.gate = gate.future;
    unawaited(controller.refreshData());
    await Future<void>.delayed(Duration.zero);

    expect(controller.state.value, isA<Refreshing<ProjectDiagnostics>>());
    expect(controller.state.value.dataOrNull, isNotNull);

    gate.complete();
    await Future<void>.delayed(Duration.zero);
  });

  test('a failed refresh keeps the previous values as stale', () async {
    final controller = build();
    await controller.load();
    final loaded = controller.state.value.dataOrNull;

    diagnostics.projectDiagnosticsError = const ServerError();
    await controller.refreshData();

    final state = controller.state.value;
    expect(state, isA<Failure<ProjectDiagnostics>>());
    expect((state as Failure<ProjectDiagnostics>).stale, loaded);
  });

  test('stale runs are read per project, never summed globally', () async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(staleAgentRuns: 3);
    final controller = build();

    await controller.load();

    expect(controller.state.value.dataOrNull?.staleAgentRuns, 3);
    expect(
      diagnostics.calls.where((c) => c.startsWith('projectDiagnostics')).length,
      1,
      reason: 'one project scope, one request — no cross-project aggregation',
    );
  });

  test('the recorded failure is surfaced with its category', () async {
    diagnostics.projectDiagnostics = testProjectDiagnostics(
      lastFailureAtUtc: DateTime.utc(2026, 9, 19, 11),
      lastFailureCategory: FailureCategory.build,
      lastFailureMessage: 'dotnet build failed with 2 errors.',
    );
    final controller = build();

    await controller.load();

    final data = controller.state.value.dataOrNull!;
    expect(data.hasRecentFailure, isTrue);
    expect(data.lastFailureCategory, FailureCategory.build);
    expect(data.lastFailureMessage, 'dotnet build failed with 2 errors.');
  });

  test('onClose cancels the in-flight read', () async {
    final gate = Completer<void>();
    diagnostics.gate = gate.future;
    final controller = build();

    unawaited(controller.load());
    await Future<void>.delayed(Duration.zero);
    controller.onClose();

    gate.complete();
    await Future<void>.delayed(Duration.zero);

    expect(controller.state.value, isNot(isA<Success<ProjectDiagnostics>>()));
  });
}
