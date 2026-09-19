import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/errors/error_mapper.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

/// Maps the exact `code` values `ApiExceptionMapper` emits on the backend
/// (see `docs/flutter-api-contract.md` §1) onto the client error model.
void main() {
  const mapper = ErrorMapper();

  final requestOptions = RequestOptions(path: '/api/projects');

  DioException problem(int status, String code, {String? detail}) {
    return DioException(
      requestOptions: requestOptions,
      type: DioExceptionType.badResponse,
      response: Response<Map<String, dynamic>>(
        requestOptions: requestOptions,
        statusCode: status,
        data: <String, dynamic>{
          'type': 'https://httpstatuses.com/$status',
          'title': 'Test',
          'status': status,
          'detail': detail ?? 'detail text',
          'code': code,
          'correlationId': '11111111-2222-3333-4444-555555555555',
          'traceId': '11111111-2222-3333-4444-555555555555',
        },
        headers: Headers.fromMap(<String, List<String>>{
          'x-correlation-id': <String>['abc-123'],
        }),
      ),
    );
  }

  group('backend code wins over the bare status', () {
    final cases = <String, Type>{
      'not_found': NotFoundError,
      'validation_error': ValidationError,
      'conflict': ConflictError,
      'concurrency_conflict': ConflictError,
      'forbidden': ForbiddenError,
      'timeout': TimeoutError,
      'too_many_requests': RateLimitError,
      'service_unavailable': ProviderFailure,
      'database_unavailable': ProviderFailure,
      'database_error': ServerError,
      'internal_error': ServerError,
    };

    cases.forEach((code, expectedType) {
      test('$code maps to $expectedType', () {
        final error = mapper.map(problem(500, code));
        expect(error.runtimeType, expectedType);
        expect(error.backendCode, code);
      });
    });
  });

  group('status fallback when no code is present', () {
    final cases = <int, Type>{
      400: ValidationError,
      401: AuthenticationError,
      403: ForbiddenError,
      404: NotFoundError,
      408: TimeoutError,
      409: ConflictError,
      429: RateLimitError,
      502: ProviderFailure,
      503: ProviderFailure,
      504: TimeoutError,
      500: ServerError,
    };

    cases.forEach((status, expectedType) {
      test('$status maps to $expectedType', () {
        final error = mapper.map(
          DioException(
            requestOptions: requestOptions,
            type: DioExceptionType.badResponse,
            response: Response<String>(
              requestOptions: requestOptions,
              statusCode: status,
              data: 'not a problem document',
            ),
          ),
        );
        expect(error.runtimeType, expectedType);
      });
    });
  });

  group('transport failures', () {
    test('connection error with no response is a NetworkError', () {
      final error = mapper.map(
        DioException(
          requestOptions: requestOptions,
          type: DioExceptionType.connectionError,
          message: 'Connection refused',
        ),
      );
      expect(error, isA<NetworkError>());
      expect(error.isRetryable, isTrue);
    });

    test('each timeout type maps to TimeoutError', () {
      for (final type in const [
        DioExceptionType.connectionTimeout,
        DioExceptionType.sendTimeout,
        DioExceptionType.receiveTimeout,
      ]) {
        final error = mapper.map(
          DioException(requestOptions: requestOptions, type: type),
        );
        expect(error, isA<TimeoutError>(), reason: '$type');
      }
    });

    test('a bad certificate is a NetworkError, not a server error', () {
      final error = mapper.map(
        DioException(
          requestOptions: requestOptions,
          type: DioExceptionType.badCertificate,
        ),
      );
      expect(error, isA<NetworkError>());
    });

    test('a non-Dio object becomes UnknownError', () {
      expect(mapper.map(StateError('boom')), isA<UnknownError>());
    });

    test('an AppError passes through unchanged', () {
      const original = ConflictError(technicalDetail: 'x');
      expect(mapper.map(original), same(original));
    });
  });

  group('diagnostics', () {
    test('the correlation header is preferred over the body value', () {
      final error = mapper.map(problem(409, 'conflict'));
      expect(error.correlationId, 'abc-123');
    });

    test('the sanitized server detail is carried for the operator view', () {
      final error = mapper.map(
        problem(409, 'conflict', detail: 'Project cannot be approved'),
      );
      expect(error.technicalDetail, 'Project cannot be approved');
    });

    test('every error exposes a translation key, never a raw message', () {
      final errors = <AppError>[
        const NetworkError(),
        const TimeoutError(),
        const AuthenticationError(),
        const ForbiddenError(),
        const ValidationError(),
        const NotFoundError(),
        const ConflictError(),
        const RateLimitError(),
        const ProviderFailure(),
        const ServerError(),
        const UnknownError(),
      ];

      for (final error in errors) {
        expect(error.messageKey, startsWith('error.'));
      }
    });
  });
}
