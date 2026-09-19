import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/data/repositories/api_project_repository.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/test_app.dart';

/// Dependency injection is the seam the whole application hangs on: every
/// repository must resolve **by its interface**, and they must all share one
/// `ApiClient` so timeouts, headers and the operational key are configured
/// once.
void main() {
  setUpAll(initializeTestLocalization);

  setUp(() async {
    await registerTestServices();
    AppBindings().dependencies();
  });

  tearDown(() async => Get.deleteAll(force: true));

  test('every repository resolves by its interface', () {
    expect(Get.find<ProjectRepository>(), isA<ProjectRepository>());
    expect(Get.find<WorkItemRepository>(), isA<WorkItemRepository>());
    expect(Get.find<AgentRunRepository>(), isA<AgentRunRepository>());
    expect(Get.find<DiagnosticsRepository>(), isA<DiagnosticsRepository>());
    expect(Get.find<OperationsRepository>(), isA<OperationsRepository>());
    expect(Get.find<ApprovalRepository>(), isA<ApprovalRepository>());
  });

  test('the API implementation is reachable only through the interface', () {
    // A controller asks for the contract and receives the API-backed one; it
    // never names the concrete class, which is what lets a test swap it.
    expect(Get.find<ProjectRepository>(), isA<ApiProjectRepository>());
  });

  test('one ApiClient is shared by the whole application', () {
    final first = Get.find<ApiClient>();
    final second = Get.find<ApiClient>();

    expect(identical(first, second), isTrue);
    expect(first.baseUrl, Get.find<AppConfig>().apiBaseUrl);
  });

  test('the client is configured from AppConfig', () {
    final client = Get.find<ApiClient>();
    final config = Get.find<AppConfig>();

    expect(client.dio.options.baseUrl, config.apiBaseUrl);
    expect(client.dio.options.connectTimeout, config.connectTimeout);
    expect(client.dio.options.receiveTimeout, config.receiveTimeout);
  });

  test('the operational key is a header, never a query parameter', () {
    final client = Get.find<ApiClient>()..setOperationsKey('secret-value');

    expect(client.dio.options.headers['X-Operations-Key'], 'secret-value');

    client.setOperationsKey(null);
    expect(client.dio.options.headers.containsKey('X-Operations-Key'), isFalse);
  });
}
