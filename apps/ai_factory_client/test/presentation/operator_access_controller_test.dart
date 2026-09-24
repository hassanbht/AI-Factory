import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/network/api_headers.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/presentation/controllers/operator_access_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

/// The operational credential's whole lifecycle.
///
/// No test here uses a real operator key: the value is a synthetic string and
/// the store is in-memory, so the suite never touches a platform keystore.
void main() {
  const fakeKey = 'test-operational-key-0001';

  late ApiClient client;

  ApiClient buildClient() => ApiClient(
    config: const AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: 'http://localhost:5223',
      connectTimeout: Duration(seconds: 10),
      sendTimeout: Duration(seconds: 30),
      receiveTimeout: Duration(seconds: 30),
    ),
  );

  /// The header the key would actually be sent in, or null when none is set.
  Object? sentHeader() => client.dio.options.headers[ApiHeaders.operationsKey];

  setUp(() {
    Get.testMode = true;
    client = buildClient();
  });

  tearDown(() async => Get.deleteAll(force: true));

  group('restore', () {
    test('applies a stored key to the API client', () async {
      final store = InMemoryOperationalCredentialStore(fakeKey);
      final controller = OperatorAccessController(store: store, client: client)
        ..onInit();
      await Future<void>.delayed(Duration.zero);

      expect(controller.hasKey.value, isTrue);
      expect(sentHeader(), fakeKey);
    });

    test('an empty store leaves the client without a key', () async {
      final controller = OperatorAccessController(
        store: InMemoryOperationalCredentialStore(),
        client: client,
      )..onInit();
      await Future<void>.delayed(Duration.zero);

      expect(controller.hasKey.value, isFalse);
      expect(sentHeader(), isNull);
    });

    test(
      'a keystore failure is reported without crashing the screen',
      () async {
        final controller = OperatorAccessController(
          store: _ThrowingCredentialStore(),
          client: client,
        );

        await controller.restore();

        expect(controller.hasKey.value, isFalse);
        expect(controller.storageError.value, isNotNull);
        expect(
          sentHeader(),
          isNull,
          reason: 'no key may be sent after a failure',
        );
      },
    );
  });

  group('saveKey', () {
    test('stores the key and sends it in the operational header', () async {
      final store = InMemoryOperationalCredentialStore();
      final controller = OperatorAccessController(store: store, client: client);

      final saved = await controller.saveKey(fakeKey);

      expect(saved, isTrue);
      expect(await store.read(), fakeKey);
      expect(sentHeader(), fakeKey);
      expect(controller.hasKey.value, isTrue);
    });

    test('trims surrounding whitespace before storing', () async {
      final store = InMemoryOperationalCredentialStore();
      final controller = OperatorAccessController(store: store, client: client);

      await controller.saveKey('  $fakeKey  ');

      expect(await store.read(), fakeKey);
    });

    test(
      'refuses a blank key rather than storing an empty credential',
      () async {
        final store = InMemoryOperationalCredentialStore();
        final controller = OperatorAccessController(
          store: store,
          client: client,
        );

        final saved = await controller.saveKey('   ');

        expect(saved, isFalse);
        expect(await store.read(), isNull);
        expect(controller.hasKey.value, isFalse);
      },
    );

    test(
      'a keystore write failure reports an error and stores nothing',
      () async {
        final controller = OperatorAccessController(
          store: _ThrowingCredentialStore(),
          client: client,
        );

        final saved = await controller.saveKey(fakeKey);

        expect(saved, isFalse);
        expect(controller.storageError.value, isNotNull);
        expect(controller.hasKey.value, isFalse);
      },
    );

    test('the error never contains the key itself', () async {
      final controller = OperatorAccessController(
        store: _ThrowingCredentialStore(),
        client: client,
      );

      await controller.saveKey(fakeKey);

      final detail = controller.storageError.value?.technicalDetail ?? '';
      expect(
        detail.contains(fakeKey),
        isFalse,
        reason: 'a credential must never be interpolated into an error',
      );
    });
  });

  group('clearKey', () {
    test('forgets the key and stops sending the header', () async {
      final store = InMemoryOperationalCredentialStore(fakeKey);
      final controller = OperatorAccessController(store: store, client: client);
      await controller.restore();
      expect(sentHeader(), fakeKey);

      await controller.clearKey();

      expect(await store.read(), isNull);
      expect(sentHeader(), isNull);
      expect(controller.hasKey.value, isFalse);
    });

    test('drops the live header even when the keystore delete fails', () async {
      final controller = OperatorAccessController(
        store: _ThrowingCredentialStore(),
        client: client,
      );
      client.setOperationsKey(fakeKey);

      await controller.clearKey();

      expect(
        sentHeader(),
        isNull,
        reason: 'the session must stop sending a key the operator removed',
      );
      expect(controller.hasKey.value, isFalse);
    });
  });

  test('the operational key header is on the redaction list', () {
    expect(ApiHeaders.isRedacted(ApiHeaders.operationsKey), isTrue);
  });
}

/// A keystore that is unavailable — a locked or absent platform keychain.
class _ThrowingCredentialStore implements OperationalCredentialStore {
  @override
  Future<void> clear() async => throw const _KeystoreUnavailable();

  @override
  Future<String?> read() async => throw const _KeystoreUnavailable();

  @override
  Future<void> write(String key) async => throw const _KeystoreUnavailable();

  @override
  bool get isSessionOnly => false;
}

class _KeystoreUnavailable implements Exception {
  const _KeystoreUnavailable();
}
