import 'package:get/get.dart' hide Trans;

import '../../core/errors/app_error.dart';
import '../../core/network/api_client.dart';
import '../../core/security/operational_credential_store.dart';

/// Owns the operational API key: load it, apply it, replace it, forget it.
///
/// The key is a credential, so this controller is the only place that holds
/// one, and it holds it for exactly as long as it takes to hand it to
/// [ApiClient.setOperationsKey]. It is never exposed through an observable,
/// never written into an error message, and never returned to a widget — the
/// UI observes [hasKey], a boolean, and nothing more.
///
/// Whether a key is needed at all is the backend's decision: the API requires
/// one only when `Security:OperationalEndpoints:RequireAccessKey` is enabled,
/// which is off by default. A deployment that leaves it off serves the
/// operational endpoints without any key, so this controller never demands
/// one up front — it reacts to a real `401`.
class OperatorAccessController extends GetxController {
  OperatorAccessController({
    OperationalCredentialStore? store,
    ApiClient? client,
  }) : _store = store ?? Get.find<OperationalCredentialStore>(),
       _client = client ?? Get.find<ApiClient>();

  final OperationalCredentialStore _store;
  final ApiClient _client;

  /// Whether a key is currently stored. Never the key itself.
  final RxBool hasKey = false.obs;

  /// The key is kept for this session only (Web builds).
  bool get isSessionOnly => _store.isSessionOnly;

  /// True while the keystore is being read or written.
  final RxBool isBusy = false.obs;

  /// The last failure from the keystore itself — a locked or unavailable
  /// platform keychain, not an authentication failure.
  final Rxn<AppError> storageError = Rxn<AppError>();

  @override
  void onInit() {
    super.onInit();
    restore();
  }

  /// Reads the stored key and applies it to the API client.
  ///
  /// Called on start-up and again after a `401`, so that a key saved in
  /// another window is picked up rather than assumed absent. A keystore that
  /// throws leaves the client with no key rather than crashing the screen:
  /// the operator then sees the ordinary "operator access required" state.
  Future<void> restore() async {
    isBusy.value = true;
    try {
      final key = await _store.read();
      _client.setOperationsKey(key);
      hasKey.value = key != null;
      storageError.value = null;
    } on Object catch (error) {
      _client.setOperationsKey(null);
      hasKey.value = false;
      storageError.value = UnknownError(
        technicalDetail: 'Secure storage unavailable: ${error.runtimeType}',
      );
    } finally {
      isBusy.value = false;
    }
  }

  /// Stores [key] and applies it. Returns false when the value was blank or
  /// the keystore rejected the write.
  ///
  /// A blank value is not stored: an empty credential is not a credential,
  /// and storing one would turn "no key" into "wrong key".
  Future<bool> saveKey(String key) async {
    final trimmed = key.trim();
    if (trimmed.isEmpty) return false;

    isBusy.value = true;
    try {
      await _store.write(trimmed);
      _client.setOperationsKey(trimmed);
      hasKey.value = true;
      storageError.value = null;
      return true;
    } on Object catch (error) {
      // The technical detail names the failure type only. The key itself is
      // never interpolated into an error, a log or a message.
      storageError.value = UnknownError(
        technicalDetail: 'Could not store the key: ${error.runtimeType}',
      );
      return false;
    } finally {
      isBusy.value = false;
    }
  }

  /// Forgets the key everywhere: keystore first, then the live client.
  Future<void> clearKey() async {
    isBusy.value = true;
    try {
      await _store.clear();
      storageError.value = null;
    } on Object catch (error) {
      storageError.value = UnknownError(
        technicalDetail: 'Could not clear the key: ${error.runtimeType}',
      );
    } finally {
      // The header is dropped even when the keystore write failed, so the
      // session stops sending a credential the operator asked to remove.
      _client.setOperationsKey(null);
      hasKey.value = false;
      isBusy.value = false;
    }
  }

  void dismissStorageError() => storageError.value = null;
}
