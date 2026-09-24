import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:flutter_test/flutter_test.dart';

/// Build configuration safety (Workstream AF).
///
/// `AppConfig.fromEnvironment` feeds compile-time defines and `kReleaseMode`
/// into [AppConfig.resolve]; these tests pin the rules for every
/// combination, which a debug test run could not otherwise reach.
void main() {
  AppConfig resolve({
    String env = '',
    String url = '',
    required bool release,
  }) => AppConfig.resolve(
    environmentName: env,
    baseUrlOverride: url,
    isReleaseBuild: release,
  );

  test('a debug build defaults to development and the local API', () {
    final config = resolve(release: false);
    expect(config.environment, AppEnvironment.development);
    expect(config.apiBaseUrl, 'http://localhost:5223');
    expect(config.isConfigured, isTrue);
  });

  test('a release build never defaults to the development address', () {
    final config = resolve(release: true);
    expect(config.environment, AppEnvironment.production);
    expect(config.apiBaseUrl, isEmpty);
    expect(config.isConfigured, isFalse);
  });

  test('a release build uses the address it was built with', () {
    final config = resolve(
      release: true,
      url: ' https://factory.example.test ',
    );
    expect(config.environment, AppEnvironment.production);
    expect(config.apiBaseUrl, 'https://factory.example.test');
  });

  test('production has no default address even in a debug build', () {
    final config = resolve(env: 'production', release: false);
    expect(config.apiBaseUrl, isEmpty);
    expect(config.isDevelopment, isFalse);
  });

  test('an explicit environment is honoured, case-insensitively', () {
    expect(
      resolve(env: 'Development', release: true).environment,
      AppEnvironment.development,
    );
    expect(
      resolve(env: 'PRODUCTION', release: false).environment,
      AppEnvironment.production,
    );
  });

  test('only development enables request logging', () {
    expect(resolve(release: false).isDevelopment, isTrue);
    expect(resolve(release: true).isDevelopment, isFalse);
  });
}
