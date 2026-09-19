/// Environment selection and API configuration.
///
/// Values arrive from `--dart-define` at build time, never from hard-coded
/// production infrastructure in source. The client talks only to the AI Factory
/// API: it never holds provider credentials and never calls OpenRouter, Hermes
/// or SQLite directly.
library;

enum AppEnvironment {
  development,
  production;

  static AppEnvironment fromName(String value) {
    return AppEnvironment.values.firstWhere(
      (e) => e.name.toLowerCase() == value.trim().toLowerCase(),
      orElse: () => AppEnvironment.development,
    );
  }
}

class AppConfig {
  const AppConfig({
    required this.environment,
    required this.apiBaseUrl,
    required this.connectTimeout,
    required this.sendTimeout,
    required this.receiveTimeout,
  });

  /// Reads the configuration from compile-time defines:
  ///
  /// ```
  /// flutter run -d windows \
  ///   --dart-define=AI_FACTORY_ENV=development \
  ///   --dart-define=AI_FACTORY_API_BASE_URL=http://localhost:5223
  /// ```
  factory AppConfig.fromEnvironment() {
    const envName = String.fromEnvironment(
      'AI_FACTORY_ENV',
      defaultValue: 'development',
    );
    final environment = AppEnvironment.fromName(envName);

    const baseUrlOverride = String.fromEnvironment('AI_FACTORY_API_BASE_URL');

    return AppConfig(
      environment: environment,
      apiBaseUrl: baseUrlOverride.isNotEmpty
          ? baseUrlOverride
          : _defaultBaseUrl(environment),
      connectTimeout: const Duration(seconds: 10),
      sendTimeout: const Duration(seconds: 30),
      receiveTimeout: const Duration(seconds: 30),
    );
  }

  final AppEnvironment environment;
  final String apiBaseUrl;
  final Duration connectTimeout;
  final Duration sendTimeout;
  final Duration receiveTimeout;

  /// This client's own version, kept in step with `pubspec.yaml`.
  ///
  /// It describes the **client**, never the API: the running backend's
  /// version comes from `GET /api/operations/deployment`, which is behind
  /// the operational key and belongs to the Operations screens.
  static const String clientVersion = '1.0.0';

  /// The backend release this client is written against.
  static const String targetBackendVersion = '1.0.0';

  bool get isDevelopment => environment == AppEnvironment.development;

  /// Development defaults to the API project's local launch profile. Production
  /// has no default on purpose — it must be supplied at build time, so no
  /// private infrastructure address is ever committed to source.
  static String _defaultBaseUrl(AppEnvironment environment) {
    return switch (environment) {
      AppEnvironment.development => 'http://localhost:5223',
      AppEnvironment.production => '',
    };
  }

  bool get isConfigured => apiBaseUrl.isNotEmpty;
}
