import 'package:dio/dio.dart';

/// Cancels an in-flight request without exposing Dio to the caller.
///
/// A controller creates one of these, passes it to a repository method and
/// calls [cancel] when its screen is disposed or a poll tick is superseded.
/// It never sees a `CancelToken`, which keeps the repository interfaces free
/// of a transport type and keeps controllers testable without Dio.
///
/// Single-use by design: once cancelled it stays cancelled, so a new request
/// needs a new instance — the same rule `CancelToken` itself follows.
class RequestCancellation {
  RequestCancellation();

  final CancelToken _token = CancelToken();

  bool get isCancelled => _token.isCancelled;

  /// Aborts the request this was passed to. Safe to call more than once and
  /// safe to call when no request is in flight.
  void cancel([String reason = 'Cancelled by the caller']) {
    if (_token.isCancelled) return;
    _token.cancel(reason);
  }

  /// Transport-level token. Only the networking layer reads this.
  CancelToken get token => _token;
}

/// Convenience for the data layer: unwraps a nullable cancellation.
CancelToken? cancelTokenOf(RequestCancellation? cancellation) =>
    cancellation?.token;
