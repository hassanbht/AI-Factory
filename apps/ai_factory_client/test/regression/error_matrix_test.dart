import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/core/errors/app_error.dart';
import 'package:ai_factory_client/core/polling/polling_coordinator.dart';
import 'package:ai_factory_client/core/polling/polling_policy.dart';
import 'package:ai_factory_client/data/repositories/api_project_repository.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/controllers/project_detail_controller.dart';
import 'package:dio/dio.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:fake_async/fake_async.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/api_fixtures.dart';
import '../support/fake_repositories.dart';
import '../support/test_api_client.dart';
import '../support/test_app.dart';

/// Every failure the API or the network can produce, through every layer:
///
/// 1. transport → `AppError` (real `ApiClient`, Dio, DTOs and mappers; only
///    the HTTP adapter is mocked);
/// 2. controller → retry policy and kept data (fake time);
/// 3. screen → a localized message in English and German, and no crash.
void main() {
  const id = '00000000-0000-4000-8000-000000000001';
  const path = '/api/projects/$id';

  group('1 · transport → AppError', () {
    late TestApiClient harness;
    late ProjectRepository repository;

    setUp(() {
      harness = TestApiClient.create();
      repository = ApiProjectRepository(harness.client);
    });

    final statuses = <int, (String, Matcher)>{
      400: ('validation_error', isA<ValidationError>()),
      401: ('unauthorized', isA<AuthenticationError>()),
      404: ('not_found', isA<NotFoundError>()),
      409: ('conflict', isA<ConflictError>()),
      422: ('validation_error', isA<ValidationError>()),
      429: ('rate_limited', isA<RateLimitError>()),
      500: ('internal_error', isA<ServerError>()),
    };

    for (final entry in statuses.entries) {
      test('${entry.key} → ${entry.value.$1}', () async {
        harness.adapter.onGet(
          path,
          (s) => s.reply(
            entry.key,
            problemDetails(status: entry.key, code: entry.value.$1),
          ),
        );
        await expectLater(repository.getById(id), throwsA(entry.value.$2));
      });
    }

    test('200 → the project', () async {
      harness.adapter.onGet(
        path,
        (s) => s.reply(200, ApiFixtures.object(ApiFixtures.project)),
      );
      final project = await repository.getById(id);
      expect(project.name, 'Contract Probe');
    });

    DioException transport(DioExceptionType type) => DioException(
      requestOptions: RequestOptions(path: path),
      type: type,
    );

    test('timeout → TimeoutError', () async {
      harness.adapter.onGet(
        path,
        (s) => s.throws(0, transport(DioExceptionType.receiveTimeout)),
      );
      await expectLater(repository.getById(id), throwsA(isA<TimeoutError>()));
    });

    test('network disconnect → NetworkError', () async {
      harness.adapter.onGet(
        path,
        (s) => s.throws(0, transport(DioExceptionType.connectionError)),
      );
      await expectLater(repository.getById(id), throwsA(isA<NetworkError>()));
    });

    test('malformed JSON → an AppError, not a crash', () async {
      harness.adapter.onGet(path, (s) => s.reply(200, '{"id": "tru'));
      await expectLater(repository.getById(id), throwsA(isA<AppError>()));
    });

    test('a well-formed body of the wrong shape → UnknownError', () async {
      harness.adapter.onGet(path, (s) => s.reply(200, <dynamic>[1, 2]));
      await expectLater(repository.getById(id), throwsA(isA<UnknownError>()));
    });

    test(
      'an unknown enum degrades to unknown, keeping the raw value',
      () async {
        harness.adapter.onGet(
          path,
          (s) => s.reply(200, <String, dynamic>{
            ...ApiFixtures.object(ApiFixtures.project),
            'status': 'Hibernating',
          }),
        );
        final project = await repository.getById(id);
        expect(project.status, ProjectStatus.unknown);
        expect(project.rawStatus, 'Hibernating');
      },
    );

    test('null optional fields are accepted as absent', () async {
      harness.adapter.onGet(
        path,
        (s) => s.reply(200, <String, dynamic>{
          ...ApiFixtures.object(ApiFixtures.project),
          'description': null,
          'gitRepository': null,
          'gitBranch': null,
          'lifecycleStartedAtUtc': null,
          'currentError': null,
        }),
      );
      final project = await repository.getById(id);
      expect(project.description, isNull);
      expect(project.lifecycleStartedAtUtc, isNull);
      expect(project.hasError, isFalse);
    });
  });

  group('2 · controller: retry policy and kept data', () {
    setUp(() => Get.testMode = true);
    tearDown(() async => Get.deleteAll(force: true));

    const detail = PollIntervals.defaults;
    bool stops(Duration? next) => next == null;
    bool backsOff(Duration? next) =>
        next != null && next > detail.projectDetail;

    final cases = <String, (AppError, LiveStatus, bool Function(Duration?))>{
      '400': (const ValidationError(), LiveStatus.halted, stops),
      '401': (const AuthenticationError(), LiveStatus.accessRequired, stops),
      '404': (const NotFoundError(), LiveStatus.halted, stops),
      '409': (const ConflictError(), LiveStatus.halted, stops),
      '429': (
        const RateLimitError(),
        LiveStatus.retrying,
        (next) => next == detail.maxBackoff,
      ),
      '500': (const ServerError(), LiveStatus.retrying, backsOff),
      'timeout': (const TimeoutError(), LiveStatus.offline, backsOff),
      'disconnect': (const NetworkError(), LiveStatus.offline, backsOff),
    };

    for (final entry in cases.entries) {
      test(
        '${entry.key} during a poll keeps the data and applies the policy',
        () {
          fakeAsync((async) {
            final repository = FakeProjectRepository(
              projects: [
                testProject(id: 'p1', status: ProjectStatus.implementation),
              ],
            );
            final controller = ProjectDetailController(
              projectId: 'p1',
              projects: repository,
            )..onInit();
            async.flushMicrotasks();
            controller.live.start();
            expect(controller.state.value.dataOrNull, isNotNull);

            repository.getByIdError = entry.value.$1;
            async.elapse(detail.projectDetail);

            final live = controller.live;
            expect(live.status.value, entry.value.$2);
            if (entry.value.$1 is NotFoundError) {
              // Deleted on the server: the screen says so instead of showing
              // a project that no longer exists.
              expect(controller.isNotFound, isTrue);
            } else {
              expect(
                controller.state.value.dataOrNull?.id,
                'p1',
                reason: 'the last good data is kept',
              );
            }
            final next = live.hasScheduledTick ? live.nextDelay : null;
            expect(entry.value.$3(next), isTrue, reason: 'next delay $next');
            controller.onClose();
          });
        },
      );
    }
  });

  group('3 · screen: localized message, no crash', () {
    setUpAll(initializeTestLocalization);

    final errors = <AppError>[
      const ValidationError(),
      const AuthenticationError(),
      const NotFoundError(),
      const ConflictError(),
      const RateLimitError(),
      const ServerError(),
      const TimeoutError(),
      const NetworkError(),
      const UnknownError(),
    ];

    for (final locale in const [Locale('en'), Locale('de')]) {
      for (final error in errors) {
        testWidgets(
          '${error.runtimeType} on Projects, ${locale.languageCode}',
          (tester) async {
            await registerTestServices();
            Get.put<ProjectRepository>(
              FakeProjectRepository()..listError = error,
            );
            await pumpScreen(
              tester,
              initialRoute: AppRoutes.projects,
              getPages: AppRoutes.pages,
              startLocale: locale,
            );

            expect(find.text(error.messageKey.tr()), findsWidgets);
            expect(find.text('common.retry'.tr()), findsWidgets);
            expectNoUntranslatedKeys(tester);
          },
        );
      }
    }
  });
}
