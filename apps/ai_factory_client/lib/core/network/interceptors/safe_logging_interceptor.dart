import 'dart:developer' as developer;

import 'package:dio/dio.dart';

import '../api_headers.dart';

/// Request/response logging that cannot leak a credential.
///
/// Header values in [ApiHeaders.redacted] are replaced before anything is
/// written, and request/response bodies are never logged — a body can contain
/// operator data, and the diagnostics the client actually needs (method, path,
/// status, duration, correlation id) do not require it.
class SafeLoggingInterceptor extends Interceptor {
  SafeLoggingInterceptor({
    required this.enabled,
    void Function(String message)? sink,
  }) : _sink = sink ?? _defaultSink;

  final bool enabled;
  final void Function(String message) _sink;

  static const String _stopwatchKey = 'requestStartedAt';

  static void _defaultSink(String message) =>
      developer.log(message, name: 'api');

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    if (enabled) {
      options.extra[_stopwatchKey] = DateTime.now();
      _sink('-> ${options.method} ${options.uri} ${_headers(options.headers)}');
    }
    handler.next(options);
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    if (enabled) {
      _sink(
        '<- ${response.statusCode} ${response.requestOptions.method} '
        '${response.requestOptions.uri}${_elapsed(response.requestOptions)}',
      );
    }
    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    if (enabled) {
      final correlationId =
          err.response?.headers.value(ApiHeaders.correlationId) ?? '-';
      _sink(
        '!! ${err.type.name} ${err.response?.statusCode ?? '-'} '
        '${err.requestOptions.method} ${err.requestOptions.uri} '
        'correlationId=$correlationId${_elapsed(err.requestOptions)}',
      );
    }
    handler.next(err);
  }

  String _elapsed(RequestOptions options) {
    final startedAt = options.extra[_stopwatchKey];
    if (startedAt is! DateTime) return '';
    return ' (${DateTime.now().difference(startedAt).inMilliseconds} ms)';
  }

  /// Redacted header rendering — the only place header values are stringified.
  String _headers(Map<String, dynamic> headers) {
    if (headers.isEmpty) return '';
    final rendered = headers.entries
        .map(
          (e) => '${e.key}=${ApiHeaders.isRedacted(e.key) ? '***' : e.value}',
        )
        .join(', ');
    return '[$rendered]';
  }
}
