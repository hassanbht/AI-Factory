import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/network/api_headers.dart';
import 'package:ai_factory_client/core/network/interceptors/safe_logging_interceptor.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/presentation/controllers/operator_access_controller.dart';
import 'package:ai_factory_client/presentation/widgets/operator_access_panel.dart';
import 'package:dio/dio.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../support/test_app.dart';

/// Where the operational key may and may not go (Workstream T, `G-OPS-KEY-UX`).
///
/// The key under test is assembled at runtime; no real credential is used.
void main() {
  setUpAll(initializeTestLocalization);

  final key = ['op', 'key', 'for', 'tests', 'only'].join('-');

  ApiClient client() => ApiClient(
    config: const AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: 'http://localhost:5223',
      connectTimeout: Duration(seconds: 1),
      sendTimeout: Duration(seconds: 1),
      receiveTimeout: Duration(seconds: 1),
    ),
  );

  setUp(() {
    SharedPreferences.setMockInitialValues(<String, Object>{});
    FlutterSecureStorage.setMockInitialValues(<String, String>{});
  });

  group('storage', () {
    test('the key goes to flutter_secure_storage and nowhere else', () async {
      const storage = FlutterSecureStorage();
      final store = SecureOperationalCredentialStore(storage: storage);
      final access = OperatorAccessController(store: store, client: client());

      expect(await access.saveKey(key), isTrue);

      expect(await storage.readAll(), containsValue(key));
      final prefs = await SharedPreferences.getInstance();
      for (final name in prefs.getKeys()) {
        expect('${prefs.get(name)}', isNot(contains(key)), reason: name);
      }
    });

    test('clearing removes it from the keystore and the live header', () async {
      const storage = FlutterSecureStorage();
      final api = client();
      final access = OperatorAccessController(
        store: SecureOperationalCredentialStore(storage: storage),
        client: api,
      );
      await access.saveKey(key);
      expect(api.dio.options.headers[ApiHeaders.operationsKey], key);

      await access.clearKey();

      expect(await storage.readAll(), isEmpty);
      expect(
        api.dio.options.headers.containsKey(ApiHeaders.operationsKey),
        isFalse,
      );
      expect(access.hasKey.value, isFalse);
    });

    test('Web builds keep the key for the session only', () async {
      final store = credentialStoreFor(isWeb: true);
      expect(store, isA<InMemoryOperationalCredentialStore>());
      expect(store.isSessionOnly, isTrue);

      await store.write(key);
      expect(await store.read(), key);
      // Nothing reached a persistent store.
      expect(await const FlutterSecureStorage().readAll(), isEmpty);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getKeys(), isEmpty);
    });

    test('desktop builds use the platform keystore', () {
      final store = credentialStoreFor(isWeb: false);
      expect(store, isA<SecureOperationalCredentialStore>());
      expect(store.isSessionOnly, isFalse);
    });
  });

  group('logs', () {
    test(
      'request and error lines redact the key and never log a body',
      () async {
        final lines = <String>[];
        final dio = Dio(BaseOptions(baseUrl: 'http://localhost:5223'));
        final adapter = DioAdapter(dio: dio);
        dio.interceptors.add(
          SafeLoggingInterceptor(enabled: true, sink: lines.add),
        );
        dio.options.headers[ApiHeaders.operationsKey] = key;

        adapter
          ..onGet(
            '/api/operations/deployment',
            (s) => s.reply(200, <String, String>{'k': key}),
          )
          ..onGet(
            '/api/audit-log',
            (s) => s.reply(401, <String, String>{'detail': key}),
          );

        await dio.get<dynamic>('/api/operations/deployment');
        await expectLater(
          dio.get<dynamic>('/api/audit-log'),
          throwsA(isA<DioException>()),
        );

        expect(lines, hasLength(4));
        expect(lines.join('\n'), isNot(contains(key)));
        expect(lines.first, contains('${ApiHeaders.operationsKey}=***'));
      },
    );

    test('a production build does not log requests at all', () {
      final production = ApiClient(
        config: AppConfig.resolve(
          environmentName: '',
          baseUrlOverride: 'https://factory.example.test',
          isReleaseBuild: true,
        ),
      );
      final logging = production.dio.interceptors
          .whereType<SafeLoggingInterceptor>()
          .single;
      expect(logging.enabled, isFalse);
    });
  });

  group('screen', () {
    Future<OperatorAccessController> pumpPanel(
      WidgetTester tester, {
      required OperationalCredentialStore store,
    }) async {
      await registerTestServices();
      final access = OperatorAccessController(store: store, client: client());
      await pumpScreen(
        tester,
        home: Scaffold(
          body: SingleChildScrollView(
            child: OperatorAccessPanel(controller: access),
          ),
        ),
      );
      return access;
    }

    testWidgets('the key is never shown after it is saved', (tester) async {
      final store = InMemoryOperationalCredentialStore();
      final access = await pumpPanel(tester, store: store);

      await tester.enterText(find.byType(TextField), key);
      await tester.tap(find.text('operatorAccess.save'.tr()));
      await tester.pumpAndSettle();

      expect(await store.read(), key);
      expect(access.hasKey.value, isTrue);
      expect(find.text(key), findsNothing);
      for (final field in tester.widgetList<EditableText>(
        find.byType(EditableText),
      )) {
        expect(field.controller.text, isNot(contains(key)));
      }
      expect(find.text('operatorAccess.keyStored'.tr()), findsOneWidget);
    });

    testWidgets('a session-only store says so; the keystore does not', (
      tester,
    ) async {
      await pumpPanel(
        tester,
        store: InMemoryOperationalCredentialStore.session(),
      );
      expect(find.text('operatorAccess.sessionOnly'.tr()), findsOneWidget);

      await pumpPanel(tester, store: InMemoryOperationalCredentialStore());
      expect(find.text('operatorAccess.sessionOnly'.tr()), findsNothing);
    });
  });
}
