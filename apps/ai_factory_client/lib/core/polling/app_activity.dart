import 'package:flutter/widgets.dart';
import 'package:get/get.dart' hide Trans;

/// Whether the application is in the foreground, as one observable flag.
///
/// Polling coordinators listen to [isForeground] instead of to the Flutter
/// binding directly, so a test flips the flag itself rather than depending
/// on real operating-system lifecycle events.
///
/// Mapping from [AppLifecycleState]:
///
/// * `resumed`, `inactive` — foreground. On desktop, `inactive` is reported
///   when the window merely loses focus while still visible; an operator
///   watching the console on a second monitor must keep seeing live data.
/// * `hidden`, `paused`, `detached` — background: minimized, covered, or
///   being torn down. Polling pauses and resumes with an immediate refresh.
class AppActivity {
  AppActivity();

  /// Follows the real application lifecycle for as long as it lives.
  factory AppActivity.fromBinding() {
    final activity = AppActivity();
    activity._listener = AppLifecycleListener(
      onStateChange: activity.onLifecycleState,
    );
    return activity;
  }

  final RxBool isForeground = true.obs;

  AppLifecycleListener? _listener;

  void onLifecycleState(AppLifecycleState state) {
    isForeground.value = switch (state) {
      AppLifecycleState.resumed || AppLifecycleState.inactive => true,
      AppLifecycleState.hidden ||
      AppLifecycleState.paused ||
      AppLifecycleState.detached => false,
    };
  }

  void dispose() {
    _listener?.dispose();
    _listener = null;
  }

  /// The registered instance, or null where none is registered — a unit test
  /// that builds a controller directly has no application lifecycle.
  static AppActivity? maybeFind() =>
      Get.isRegistered<AppActivity>() ? Get.find<AppActivity>() : null;
}
