import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Where the operational API key lives.
///
/// The key is a **separate credential from every provider secret**: it
/// authenticates this operator against the AI Factory API's operational
/// endpoints and nothing else. No OpenRouter key, Hermes credential, Docker
/// credential or provider secret is ever stored here, read here, or accepted
/// as a substitute.
///
/// An interface rather than a direct `flutter_secure_storage` call so that
/// repositories and controllers stay testable without a platform keystore —
/// and so no layer above this one ever imports the storage package
/// (Workstream J).
abstract class OperationalCredentialStore {
  /// The stored key, or null when none has been saved.
  Future<String?> read();

  /// Replaces the stored key. Callers trim before saving; an empty value is
  /// rejected rather than stored as a blank credential.
  Future<void> write(String key);

  /// Forgets the key. Idempotent — clearing an empty store is not an error.
  Future<void> clear();

  /// True when the key lives only as long as this process (or browser tab)
  /// and is never written anywhere. The UI says so, so the operator is not
  /// surprised to be asked again.
  bool get isSessionOnly;
}

/// The store for the platform the application runs on.
///
/// Web builds hold the key in memory only. `flutter_secure_storage` on the
/// web writes to `localStorage`, encrypted with a key that sits in the same
/// `localStorage` — that is persistence without protection, so a browser
/// never silently keeps an operational key (`G-OPS-KEY-UX`).
OperationalCredentialStore credentialStoreFor({required bool isWeb}) => isWeb
    ? InMemoryOperationalCredentialStore.session()
    : SecureOperationalCredentialStore();

/// Platform-keystore implementation: Keychain on macOS/iOS, the Windows
/// credential store via DPAPI, `EncryptedSharedPreferences` on Android.
///
/// The key is never written to `SharedPreferences`, a JSON file, a Dart
/// constant or a log line. It is also never placed in a URL or query string:
/// the only place it ever appears is the request header the backend defines
/// (`ApiHeaders.operationsKey`), whose value the logging interceptor redacts.
class SecureOperationalCredentialStore implements OperationalCredentialStore {
  SecureOperationalCredentialStore({FlutterSecureStorage? storage})
    : _storage =
          storage ??
          const FlutterSecureStorage(
            aOptions: AndroidOptions(encryptedSharedPreferences: true),
          );

  static const String _key = 'ai_factory.operational_key';

  final FlutterSecureStorage _storage;

  @override
  Future<String?> read() async {
    final value = await _storage.read(key: _key);
    if (value == null || value.isEmpty) return null;
    return value;
  }

  @override
  Future<void> write(String key) => _storage.write(key: _key, value: key);

  @override
  Future<void> clear() => _storage.delete(key: _key);

  @override
  bool get isSessionOnly => false;
}

/// In-memory store for tests and for platforms without a keystore.
///
/// Holds the value for the life of the process only. Tests use it so that a
/// suite never touches a real keychain and never needs a real credential.
class InMemoryOperationalCredentialStore implements OperationalCredentialStore {
  InMemoryOperationalCredentialStore([this._value]) : isSessionOnly = false;

  /// The Web store: nothing survives a reload, and the UI says so.
  InMemoryOperationalCredentialStore.session() : isSessionOnly = true;

  String? _value;

  @override
  final bool isSessionOnly;

  @override
  Future<String?> read() async => _value;

  @override
  Future<void> write(String key) async => _value = key;

  @override
  Future<void> clear() async => _value = null;
}
