import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

/// An [ApiClient] wired to a mocked transport.
///
/// The real `ApiClient` is used — base options, interceptors, error mapping
/// and all — and only the bottom-most layer, the HTTP adapter, is replaced.
/// A repository test therefore exercises the same stack production does:
///
///     Repository -> ApiClient -> Dio -> (mocked response) -> DTO -> Domain
class TestApiClient {
  TestApiClient._(this.client, this.adapter);

  factory TestApiClient.create({String baseUrl = 'http://localhost:5223'}) {
    final config = AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: baseUrl,
      connectTimeout: const Duration(seconds: 1),
      sendTimeout: const Duration(seconds: 1),
      receiveTimeout: const Duration(seconds: 1),
    );

    final client = ApiClient(config: config);
    final adapter = DioAdapter(
      dio: client.dio,
      matcher: const FullHttpRequestMatcher(),
    );

    return TestApiClient._(client, adapter);
  }

  final ApiClient client;
  final DioAdapter adapter;
}

/// Builds the RFC 7807 body the API returns for a failure, in the exact shape
/// `ApiExceptionMapper` produces.
Map<String, dynamic> problemDetails({
  required int status,
  required String code,
  String detail = 'Something went wrong.',
  String correlationId = '11111111-2222-3333-4444-555555555555',
}) => <String, dynamic>{
  'type': 'https://httpstatuses.com/$status',
  'title': 'Test failure',
  'status': status,
  'detail': detail,
  'instance': '/api/test',
  'correlationId': correlationId,
  'traceId': correlationId,
  'code': code,
  'message': detail,
};
