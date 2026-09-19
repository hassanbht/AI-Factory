import 'package:dio/dio.dart';

import '../config/app_config.dart';
import '../errors/app_error.dart';
import '../errors/error_mapper.dart';
import 'api_headers.dart';
import 'interceptors/correlation_interceptor.dart';
import 'interceptors/safe_logging_interceptor.dart';

/// The single configuration point for HTTP access to the AI Factory API.
///
/// Repositories depend on this; widgets never do. Every method throws an
/// [AppError] — a `DioException` never escapes this class, so no upper layer
/// has to know what HTTP is.
///
/// The client talks only to the AI Factory API. It never calls a model
/// provider, Hermes or a database directly, and it holds no provider
/// credentials.
class ApiClient {
  ApiClient({
    required AppConfig config,
    Dio? dio,
    this.errorMapper = const ErrorMapper(),
  }) : _config = config,
       dio = dio ?? _build(config);

  final AppConfig _config;

  /// Exposed so a repository test can assert how a failure was classified.
  final ErrorMapper errorMapper;

  /// Exposed so repository tests can install a mock adapter.
  final Dio dio;

  static Dio _build(AppConfig config) {
    final dio = Dio(
      BaseOptions(
        baseUrl: config.apiBaseUrl,
        connectTimeout: config.connectTimeout,
        sendTimeout: config.sendTimeout,
        receiveTimeout: config.receiveTimeout,
        responseType: ResponseType.json,
        headers: const <String, String>{'Accept': 'application/json'},
        // The API answers errors with application/problem+json; letting Dio
        // surface them as DioException keeps one error path.
        validateStatus: (status) =>
            status != null && status >= 200 && status < 300,
      ),
    );

    dio.interceptors.addAll(<Interceptor>[
      CorrelationInterceptor(),
      SafeLoggingInterceptor(enabled: config.isDevelopment),
    ]);

    return dio;
  }

  /// Sets or clears the operational key used by `/api/operations/*`,
  /// `/api/audit-log` and the project diagnostics endpoints.
  ///
  /// The value is a secret: it is never logged (see
  /// [ApiHeaders.redacted]) and never placed in a URL or query string.
  void setOperationsKey(String? key) {
    if (key == null || key.isEmpty) {
      dio.options.headers.remove(ApiHeaders.operationsKey);
      return;
    }
    dio.options.headers[ApiHeaders.operationsKey] = key;
  }

  Future<T> get<T>(
    String path, {
    Map<String, dynamic>? queryParameters,
    CancelToken? cancelToken,
  }) {
    return _send<T>(
      () => dio.get<T>(
        path,
        queryParameters: queryParameters,
        cancelToken: cancelToken,
      ),
    );
  }

  Future<T> post<T>(
    String path, {
    Object? body,
    Map<String, dynamic>? queryParameters,
    CancelToken? cancelToken,
  }) {
    return _send<T>(
      () => dio.post<T>(
        path,
        data: body,
        queryParameters: queryParameters,
        cancelToken: cancelToken,
      ),
    );
  }

  Future<T> patch<T>(String path, {Object? body, CancelToken? cancelToken}) {
    return _send<T>(
      () => dio.patch<T>(path, data: body, cancelToken: cancelToken),
    );
  }

  Future<T> delete<T>(String path, {CancelToken? cancelToken}) {
    return _send<T>(() => dio.delete<T>(path, cancelToken: cancelToken));
  }

  /// Typed helpers. Repositories use these rather than casting `dynamic`,
  /// so a response whose shape changed fails in the data layer with a clear
  /// error instead of somewhere inside a mapper.
  Future<Map<String, dynamic>> getJson(
    String path, {
    Map<String, dynamic>? queryParameters,
    CancelToken? cancelToken,
  }) async {
    final data = await get<dynamic>(
      path,
      queryParameters: queryParameters,
      cancelToken: cancelToken,
    );
    return _asJsonObject(data, path);
  }

  Future<List<Map<String, dynamic>>> getJsonList(
    String path, {
    Map<String, dynamic>? queryParameters,
    CancelToken? cancelToken,
  }) async {
    final data = await get<dynamic>(
      path,
      queryParameters: queryParameters,
      cancelToken: cancelToken,
    );
    return _asJsonArray(data, path);
  }

  /// For `/health/*`, which answers `text/plain`, not JSON.
  Future<String> getText(String path, {CancelToken? cancelToken}) async {
    final data = await get<dynamic>(path, cancelToken: cancelToken);
    return data?.toString().trim() ?? '';
  }

  Future<Map<String, dynamic>> postJson(
    String path, {
    Object? body,
    CancelToken? cancelToken,
  }) async {
    final data = await post<dynamic>(
      path,
      body: body,
      cancelToken: cancelToken,
    );
    return _asJsonObject(data, path);
  }

  Future<Map<String, dynamic>> patchJson(
    String path, {
    Object? body,
    CancelToken? cancelToken,
  }) async {
    final data = await patch<dynamic>(
      path,
      body: body,
      cancelToken: cancelToken,
    );
    return _asJsonObject(data, path);
  }

  Map<String, dynamic> _asJsonObject(Object? data, String path) {
    if (data is Map<String, dynamic>) return data;
    if (data is Map) return Map<String, dynamic>.from(data);
    throw UnknownError(
      technicalDetail:
          'Expected a JSON object from $path but received ${data.runtimeType}.',
    );
  }

  List<Map<String, dynamic>> _asJsonArray(Object? data, String path) {
    if (data is! List) {
      throw UnknownError(
        technicalDetail:
            'Expected a JSON array from $path but received ${data.runtimeType}.',
      );
    }
    return data
        .map((element) => _asJsonObject(element, path))
        .toList(growable: false);
  }

  Future<T> _send<T>(Future<Response<T>> Function() request) async {
    try {
      final response = await request();
      return response.data as T;
    } on Object catch (error) {
      throw errorMapper.map(error);
    }
  }

  String get baseUrl => _config.apiBaseUrl;
}
