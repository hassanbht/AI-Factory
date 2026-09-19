/// Header names the AI Factory API defines.
///
/// `CorrelationMiddleware.HeaderName` on the backend is `X-Correlation-ID`;
/// the operational key header name is configurable server-side but defaults to
/// `X-Operations-Key`.
class ApiHeaders {
  const ApiHeaders._();

  static const String correlationId = 'X-Correlation-ID';
  static const String operationsKey = 'X-Operations-Key';

  /// Header values that must never reach a log sink.
  static const Set<String> redacted = <String>{
    'authorization',
    'x-operations-key',
    'cookie',
    'set-cookie',
    'proxy-authorization',
  };

  static bool isRedacted(String headerName) =>
      redacted.contains(headerName.toLowerCase());
}
