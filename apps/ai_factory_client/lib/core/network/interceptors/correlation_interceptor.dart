import 'dart:math';

import 'package:dio/dio.dart';

import '../api_headers.dart';

/// Attaches a client-generated `X-Correlation-ID` to every request.
///
/// The backend accepts the header and echoes it on the response, so an
/// operator can paste the id straight into `/api/operations/{correlationId}`
/// and see the server-side timeline of exactly that call.
class CorrelationInterceptor extends Interceptor {
  CorrelationInterceptor({Random? random}) : _random = random ?? Random();

  final Random _random;

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    options.headers.putIfAbsent(ApiHeaders.correlationId, _newCorrelationId);
    handler.next(options);
  }

  /// A RFC 4122 version 4 identifier. The backend parses the header as a GUID,
  /// so the format matters; `Random` is sufficient here because the value is a
  /// trace id, not a security token.
  String _newCorrelationId() {
    final bytes = List<int>.generate(16, (_) => _random.nextInt(256));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;

    String hex(int start, int end) => bytes
        .sublist(start, end)
        .map((b) => b.toRadixString(16).padLeft(2, '0'))
        .join();

    return '${hex(0, 4)}-${hex(4, 6)}-${hex(6, 8)}-${hex(8, 10)}-${hex(10, 16)}';
  }
}
