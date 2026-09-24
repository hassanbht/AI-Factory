import 'dart:async';

import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/core/state/view_state.dart';
import 'package:ai_factory_client/domain/models/agent_run.dart';
import 'package:ai_factory_client/domain/models/operations.dart';
import 'package:ai_factory_client/presentation/controllers/operations_controller.dart';
import 'package:ai_factory_client/presentation/controllers/operator_access_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';

/// [OperationsController] against scripted repositories.
///
/// The property under test throughout is **independence**: six endpoints,
/// six states, and no failure that takes the others with it.
void main() {
  const fakeKey = 'test-operational-key-0001';

  late FakeOperationsRepository operations;
  late FakeDiagnosticsRepository diagnostics;

  ApiClient buildClient() => ApiClient(
    config: const AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: 'http://localhost:5223',
      connectTimeout: Duration(seconds: 10),
      sendTimeout: Duration(seconds: 30),
      receiveTimeout: Duration(seconds: 30),
    ),
  );

  OperationsController build({OperationalCredentialStore? store}) {
    final access = OperatorAccessController(
      store: store ?? InMemoryOperationalCredentialStore(),
      client: buildClient(),
    );
    return OperationsController(
      operations: operations,
      diagnostics: diagnostics,
      access: access,
    );
  }

  setUp(() {
    Get.testMode = true;
    operations = FakeOperationsRepository();
    diagnostics = FakeDiagnosticsRepository();
  });

  tearDown(() async => Get.deleteAll(force: true));

  group('load', () {
    test('every section reaches Success when every endpoint answers', () async {
      final controller = build();

      await controller.load();

      expect(controller.health.value, isA<Success<SystemHealth>>());
      expect(controller.deployment.value, isA<Success<DeploymentInfo>>());
      expect(controller.database.value, isA<Success<DatabaseInfo>>());
      expect(controller.configuration.value, isA<Success<ConfigurationInfo>>());
      expect(controller.scheduler.value, isA<Success<SchedulerStatus>>());
      expect(controller.metrics.value, isA<Success<AgentMetrics>>());
      expect(controller.needsOperatorAccess, isFalse);
    });

    test('one unauthorized section leaves the other five loaded', () async {
      diagnostics.metricsError = const AuthenticationError();
      final controller = build();

      await controller.load();

      expect(
        controller.metrics.value,
        isA<Failure<AgentMetrics>>(),
        reason: 'agent metrics answered 401',
      );
      expect(
        controller.health.value,
        isA<Success<SystemHealth>>(),
        reason: 'one failing endpoint must not blank the others',
      );
      expect(controller.deployment.value, isA<Success<DeploymentInfo>>());
      expect(controller.database.value, isA<Success<DatabaseInfo>>());
      expect(controller.scheduler.value, isA<Success<SchedulerStatus>>());
      expect(controller.needsOperatorAccess, isTrue);
    });

    test('a server error on one section leaves the others loaded', () async {
      operations.deploymentError = const ServerError();
      final controller = build();

      await controller.load();

      expect(controller.deployment.value, isA<Failure<DeploymentInfo>>());
      expect(controller.database.value, isA<Success<DatabaseInfo>>());
      expect(
        controller.needsOperatorAccess,
        isFalse,
        reason: 'a 500 is not an authorization problem',
      );
    });

    test('a network failure on every section fails every section', () async {
      operations
        ..healthError = const NetworkError()
        ..deploymentError = const NetworkError()
        ..databaseError = const NetworkError()
        ..configurationError = const NetworkError()
        ..schedulerError = const NetworkError();
      diagnostics.metricsError = const NetworkError();
      final controller = build();

      await controller.load();

      expect(controller.health.value, isA<Failure<SystemHealth>>());
      expect(controller.metrics.value, isA<Failure<AgentMetrics>>());
      expect(
        controller.needsOperatorAccess,
        isFalse,
        reason: 'unreachable is not unauthorized',
      );
    });
  });

  group('401 handling', () {
    test('retries once with a stored key and succeeds', () async {
      operations
        ..databaseError = const AuthenticationError()
        ..failDatabaseOnce = true;
      final controller = build(
        store: InMemoryOperationalCredentialStore(fakeKey),
      );

      await controller.load();

      expect(
        controller.database.value,
        isA<Success<DatabaseInfo>>(),
        reason: 'the one permitted credential retry must be attempted',
      );
      expect(operations.databaseCount, 2, reason: 'exactly one extra attempt');
    });

    test('does not retry when no key is stored', () async {
      operations
        ..databaseError = const AuthenticationError()
        ..failDatabaseOnce = true;
      final controller = build(store: InMemoryOperationalCredentialStore());

      await controller.load();

      expect(
        operations.databaseCount,
        1,
        reason: 'retrying without a key would repeat the same 401',
      );
      expect(controller.database.value, isA<Failure<DatabaseInfo>>());
    });

    test('a persistent 401 stops after one retry — never loops', () async {
      operations.databaseError = const AuthenticationError();
      final controller = build(
        store: InMemoryOperationalCredentialStore(fakeKey),
      );

      await controller.load();

      expect(
        operations.databaseCount,
        2,
        reason: 'one original attempt plus exactly one retry',
      );
      expect(controller.database.value, isA<Failure<DatabaseInfo>>());
      expect(controller.needsOperatorAccess, isTrue);
    });

    test(
      'the retry budget is one per load pass, not one per section',
      () async {
        operations
          ..databaseError = const AuthenticationError()
          ..deploymentError = const AuthenticationError();
        final controller = build(
          store: InMemoryOperationalCredentialStore(fakeKey),
        );

        await controller.load();

        final total = operations.databaseCount + operations.deploymentCount;
        expect(
          total,
          3,
          reason: 'two first attempts plus a single shared retry',
        );
      },
    );
  });

  group('refresh', () {
    test('keeps previous data on screen while refreshing', () async {
      final controller = build();
      await controller.load();

      final gate = Completer<void>();
      operations.gate = gate.future;
      unawaited(controller.refreshData());
      await Future<void>.delayed(Duration.zero);

      expect(controller.database.value, isA<Refreshing<DatabaseInfo>>());
      expect(
        controller.database.value.dataOrNull,
        isNotNull,
        reason: 'a refresh must never blank a loaded section',
      );

      gate.complete();
      await Future<void>.delayed(Duration.zero);
    });

    test(
      'a section failing on refresh keeps its previous value as stale',
      () async {
        final controller = build();
        await controller.load();
        final loaded = controller.database.value.dataOrNull;

        operations.databaseError = const NetworkError();
        await controller.refreshData();

        final state = controller.database.value;
        expect(state, isA<Failure<DatabaseInfo>>());
        expect(
          (state as Failure<DatabaseInfo>).stale,
          loaded,
          reason: 'the operator keeps the last values that did load',
        );
      },
    );
  });

  test('retrying one section does not re-read the others', () async {
    operations.deploymentError = const ServerError();
    final controller = build();
    await controller.load();

    final databaseCallsBefore = operations.databaseCount;
    operations.deploymentError = null;

    await controller.retryDeployment();

    expect(controller.deployment.value, isA<Success<DeploymentInfo>>());
    expect(
      operations.databaseCount,
      databaseCallsBefore,
      reason: 'a single-section retry must not refetch the whole screen',
    );
  });

  test('a cancelled read does not publish a state after disposal', () async {
    final gate = Completer<void>();
    operations.gate = gate.future;
    final controller = build();

    unawaited(controller.load());
    await Future<void>.delayed(Duration.zero);
    controller.onClose();

    gate.complete();
    await Future<void>.delayed(Duration.zero);

    expect(controller.health.value, isNot(isA<Success<SystemHealth>>()));
  });
}
