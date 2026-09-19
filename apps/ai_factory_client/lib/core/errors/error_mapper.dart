import 'package:dio/dio.dart';

import '../../data/dto/problem_details_dto.dart';
import '../network/api_headers.dart';
import 'app_error.dart';

/// Translates transport failures into the client error model.
///
/// It reads the backend's stable `code` extension first and falls back to the
/// HTTP status, because `code` is the contract the API documents and the status
/// alone is ambiguous (409 covers both a conflict and a concurrency clash;
/// 503 covers an open circuit and a busy database).
///
/// Pure and dependency-free, so every branch is unit-tested without a server.
class ErrorMapper {
  const ErrorMapper();

  AppError map(Object error) {
    if (error is AppError) return error;
    if (error is DioException) return _fromDio(error);
    return UnknownError(technicalDetail: error.toString());
  }

  AppError _fromDio(DioException exception) {
    final response = exception.response;
    final problem = _parseProblem(response);
    final correlationId = _correlationId(response, problem);
    final detail = problem?.bestDetail;
    final status = response?.statusCode;
    final code = problem?.code;

    switch (exception.type) {
      case DioExceptionType.connectionTimeout:
      case DioExceptionType.sendTimeout:
      case DioExceptionType.receiveTimeout:
      case DioExceptionType.transformTimeout:
        return TimeoutError(
          technicalDetail: detail ?? exception.message,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case DioExceptionType.connectionError:
      case DioExceptionType.unknown:
        if (status == null) {
          return NetworkError(
            technicalDetail: detail ?? exception.message,
            correlationId: correlationId,
            backendCode: code,
          );
        }
      case DioExceptionType.cancel:
        // A cancelled request is a client-initiated, expected outcome — the
        // caller decides whether to surface it at all.
        return RequestCancelledError(
          technicalDetail: exception.message ?? 'Request cancelled',
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case DioExceptionType.badCertificate:
        return NetworkError(
          technicalDetail: detail ?? exception.message,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case DioExceptionType.badResponse:
        break;
    }

    return _fromStatus(
      status: status,
      code: code,
      detail: detail ?? exception.message,
      correlationId: correlationId,
    );
  }

  AppError _fromStatus({
    required int? status,
    required String? code,
    required String? detail,
    required String? correlationId,
  }) {
    // The backend's own discriminator wins where it is present.
    switch (code) {
      case 'not_found':
        return NotFoundError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'validation_error':
        return ValidationError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'conflict':
      case 'concurrency_conflict':
        return ConflictError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'forbidden':
        return ForbiddenError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'timeout':
        return TimeoutError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'too_many_requests':
        return RateLimitError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'service_unavailable':
      case 'database_unavailable':
        return ProviderFailure(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
      case 'database_error':
      case 'internal_error':
        return ServerError(
          technicalDetail: detail,
          correlationId: correlationId,
          statusCode: status,
          backendCode: code,
        );
    }

    return switch (status) {
      400 => ValidationError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      401 => AuthenticationError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      403 => ForbiddenError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      404 => NotFoundError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      408 => TimeoutError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      409 => ConflictError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      429 => RateLimitError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      503 || 502 => ProviderFailure(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      504 => TimeoutError(
        technicalDetail: detail,
        correlationId: correlationId,
        statusCode: status,
        backendCode: code,
      ),
      _ =>
        (status != null && status >= 500)
            ? ServerError(
                technicalDetail: detail,
                correlationId: correlationId,
                statusCode: status,
                backendCode: code,
              )
            : UnknownError(
                technicalDetail: detail,
                correlationId: correlationId,
                statusCode: status,
                backendCode: code,
              ),
    };
  }

  ProblemDetailsDto? _parseProblem(Response<dynamic>? response) {
    final data = response?.data;
    if (data is! Map) return null;
    try {
      return ProblemDetailsDto.fromJson(Map<String, dynamic>.from(data));
    } on Object {
      // A non-ProblemDetails body is not an error in itself; the status code
      // still carries enough to classify the failure.
      return null;
    }
  }

  String? _correlationId(
    Response<dynamic>? response,
    ProblemDetailsDto? problem,
  ) {
    final header = response?.headers.value(ApiHeaders.correlationId);
    if (header != null && header.isNotEmpty) return header;
    return problem?.correlationId ?? problem?.traceId;
  }
}
