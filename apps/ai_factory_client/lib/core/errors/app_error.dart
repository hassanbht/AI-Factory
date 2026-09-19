import 'package:equatable/equatable.dart';

/// The client-side error model from `docs/flutter-architecture.md` §8.
///
/// Every failure the UI can encounter is one of these. Widgets render
/// [messageKey] through the localization layer; [technicalDetail],
/// [correlationId] and [statusCode] belong to the operator-facing diagnostics
/// panel only. Raw stack traces are never carried here and never rendered.
sealed class AppError extends Equatable implements Exception {
  const AppError({
    this.technicalDetail,
    this.correlationId,
    this.statusCode,
    this.backendCode,
  });

  /// Sanitized server text (ProblemDetails `detail`). English, operator view.
  final String? technicalDetail;

  /// `X-Correlation-ID` — the join key into `/api/audit-log`.
  final String? correlationId;

  final int? statusCode;

  /// The backend's stable `code` extension (`conflict`, `not_found`, ...).
  final String? backendCode;

  /// Translation key for the message shown to a normal user.
  String get messageKey;

  /// Stable client-side discriminator, used for logging and analytics.
  String get kind;

  /// Whether retrying the identical request can plausibly succeed.
  bool get isRetryable => false;

  @override
  List<Object?> get props => [
    kind,
    technicalDetail,
    correlationId,
    statusCode,
    backendCode,
  ];

  @override
  String toString() => '$kind(status: $statusCode, code: $backendCode)';
}

class NetworkError extends AppError {
  const NetworkError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.network';

  @override
  String get kind => 'NetworkError';

  @override
  bool get isRetryable => true;
}

class TimeoutError extends AppError {
  const TimeoutError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.timeout';

  @override
  String get kind => 'TimeoutError';

  @override
  bool get isRetryable => true;
}

class AuthenticationError extends AppError {
  const AuthenticationError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.authentication';

  @override
  String get kind => 'AuthenticationError';
}

class ForbiddenError extends AppError {
  const ForbiddenError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.forbidden';

  @override
  String get kind => 'ForbiddenError';
}

class ValidationError extends AppError {
  const ValidationError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.validation';

  @override
  String get kind => 'ValidationError';
}

class NotFoundError extends AppError {
  const NotFoundError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.notFound';

  @override
  String get kind => 'NotFoundError';
}

class ConflictError extends AppError {
  const ConflictError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.conflict';

  @override
  String get kind => 'ConflictError';
}

class RateLimitError extends AppError {
  const RateLimitError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.rateLimited';

  @override
  String get kind => 'RateLimitError';

  @override
  bool get isRetryable => true;
}

/// An external dependency the Factory relies on (model provider, database,
/// an open circuit) is temporarily unavailable — HTTP 503.
class ProviderFailure extends AppError {
  const ProviderFailure({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.providerFailure';

  @override
  String get kind => 'ProviderFailure';

  @override
  bool get isRetryable => true;
}

class ServerError extends AppError {
  const ServerError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.server';

  @override
  String get kind => 'ServerError';
}

class UnknownError extends AppError {
  const UnknownError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.unknown';

  @override
  String get kind => 'UnknownError';
}

/// The caller aborted the request — a screen was disposed, or a newer poll
/// superseded this one.
///
/// Not a failure: it is the expected outcome of [RequestCancellation.cancel],
/// and a controller normally drops it silently rather than showing anything.
/// It exists as its own type precisely so it can be distinguished from a real
/// error instead of being reported as one.
class RequestCancelledError extends AppError {
  const RequestCancelledError({
    super.technicalDetail,
    super.correlationId,
    super.statusCode,
    super.backendCode,
  });

  @override
  String get messageKey => 'error.cancelled';

  @override
  String get kind => 'RequestCancelledError';
}
