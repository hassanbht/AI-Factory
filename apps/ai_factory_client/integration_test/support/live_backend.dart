import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/core/config/app_config.dart';
import 'package:ai_factory_client/core/network/api_client.dart';
import 'package:ai_factory_client/core/polling/app_activity.dart';
import 'package:ai_factory_client/core/security/operational_credential_store.dart';
import 'package:ai_factory_client/data/dto/requests.dart';
import 'package:ai_factory_client/data/repositories/api_approval_repository.dart';
import 'package:ai_factory_client/data/repositories/api_project_repository.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:ai_factory_client/domain/models/project.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Response, Trans;

import '../../test/support/test_app.dart';

/// Shared set-up for the Phase 19.10 real-backend integration tests.
///
/// Every one of them is opt-in (`--dart-define=AI_FACTORY_LIVE=true`) and
/// runs only against an API started on a **throwaway** database; the
/// procedure that proves the database path, the migrations and the
/// environment before a run is in `docs/flutter-testing.md`. None of them
/// needs, reads or stores a credential of any kind.
class LiveBackend {
  LiveBackend._(this.other);

  static const bool enabled = bool.fromEnvironment('AI_FACTORY_LIVE');
  static const String baseUrl = String.fromEnvironment(
    'AI_FACTORY_API_BASE_URL',
    defaultValue: 'http://127.0.0.1:5299',
  );

  /// "Another operator": an independent client that changes server state the
  /// screen under test is never told about.
  final ApiClient other;

  ApiProjectRepository get otherProjects => ApiProjectRepository(other);

  /// Registers the real application bindings against [baseUrl], with an
  /// in-memory credential store (never a real keystore in a test). Tests
  /// that exercise the app lifecycle add [registerAppActivity].
  static Future<LiveBackend> start() async {
    await registerTestServices();
    const config = AppConfig(
      environment: AppEnvironment.development,
      apiBaseUrl: baseUrl,
      connectTimeout: Duration(seconds: 10),
      sendTimeout: Duration(seconds: 30),
      receiveTimeout: Duration(seconds: 30),
    );
    await Get.delete<AppConfig>(force: true);
    Get.put<AppConfig>(config, permanent: true);
    AppBindings().dependencies();
    await Get.delete<OperationalCredentialStore>(force: true);
    Get.put<OperationalCredentialStore>(InMemoryOperationalCredentialStore());
    return LiveBackend._(ApiClient(config: config));
  }

  /// A project created and given an empty source by the other operator.
  Future<Project> createProject(String name) async {
    final created = await otherProjects.create(
      CreateProjectRequest(
        name: '$name ${DateTime.now().millisecondsSinceEpoch}',
        description: 'Phase 19.10 live check on a throwaway database.',
      ),
    );
    return otherProjects.initializeSource(
      created.id,
      const InitializeProjectSourceRequest(sourceType: ProjectSourceType.empty),
    );
  }

  Future<void> advance(String projectId, List<ProjectStatus> path) async {
    for (final status in path) {
      await otherProjects.updateStatus(projectId, status);
    }
  }

  /// Approves as another operator would, from another client.
  Future<void> otherApprove(String projectId) =>
      ApiApprovalRepository(other).approve(projectId, actor: 'live-19.10');

  final Map<String, String> _definitions = <String, String>{};

  /// An enabled agent definition per work-item type — the backend refuses a
  /// plan whose task types have none.
  Future<String> _definition(String type) async {
    final known = _definitions[type];
    if (known != null) return known;
    final created = await other.dio.post<Map<String, dynamic>>(
      '/api/agents/definitions',
      data: <String, Object?>{
        'name': 'live-$type-${DateTime.now().microsecondsSinceEpoch}',
        'description': 'Phase 19.10 live check',
        'agentType': type,
        'systemPrompt': 'live check',
        'enabled': true,
      },
    );
    final id = '${created.data?['id']}';
    _definitions[type] = id;
    return id;
  }

  /// Submits a two-task plan — backend-owned; the client never does this.
  Future<void> submitPlan(String projectId) async {
    await _definition('Analysis');
    await _definition('Implementation');
    const criteria = <String, Object?>{
      'version': 1,
      'criteria': [
        {'type': 'BuildPasses'},
      ],
    };
    await other.dio.post<dynamic>(
      '/api/projects/$projectId/plan',
      data: <String, Object?>{
        'summary': 'Phase 19.10 live plan',
        'tasks': [
          {
            'planKey': 'T01',
            'title': 'Analyse the requirements',
            'type': 'Analysis',
            'priority': 'High',
            'dependsOn': <String>[],
            'acceptanceCriteria': criteria,
          },
          {
            'planKey': 'T02',
            'title': 'Implement the endpoint',
            'type': 'Implementation',
            'priority': 'Normal',
            'dependsOn': ['T01'],
            'acceptanceCriteria': criteria,
          },
        ],
      },
    );
  }

  /// Queues one agent run for the project. With no Worker running it stays
  /// `Queued` — which keeps Project Detail on its fastest interval.
  Future<void> queueAgentRun(String projectId) async {
    await other.dio.post<dynamic>(
      '/api/agents/runs',
      data: <String, Object?>{
        'agentDefinitionId': await _definition('Analysis'),
        'projectId': projectId,
      },
    );
  }
}

/// Registers the real app-lifecycle listener, as `AppBindings` does in the
/// application.
void registerAppActivity() {
  if (!Get.isRegistered<AppActivity>()) {
    Get.put<AppActivity>(AppActivity.fromBinding(), permanent: true);
  }
}

/// Pumps until [condition] holds, on the real clock.
Future<Duration> waitUntil(
  WidgetTester tester,
  String what,
  bool Function() condition, {
  Duration timeout = const Duration(seconds: 40),
}) async {
  final watch = Stopwatch()..start();
  while (watch.elapsed < timeout) {
    await tester.pump(const Duration(milliseconds: 100));
    if (condition()) return watch.elapsed;
    await Future<void>.delayed(const Duration(milliseconds: 150));
  }
  throw TestFailure('Timed out after $timeout waiting for: $what');
}

/// Counts every request the application sends, how many overlap, and every
/// `429` the API answers.
class RequestMeter extends Interceptor {
  final List<(DateTime, String)> sent = <(DateTime, String)>[];
  final Map<String, int> _inFlight = <String, int>{};
  int maxPerEndpoint = 0;
  int maxTotal = 0;
  int _total = 0;
  int rateLimited = 0;
  int failures = 0;

  static final RegExp _id = RegExp('[0-9a-fA-F-]{36}');

  static String endpointOf(RequestOptions o) =>
      '${o.method} ${o.uri.path.replaceAll(_id, '{id}')}';

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    // Keyed by the concrete URL: two screens reading different projects are
    // not an overlap; the same read twice at once would be.
    final key = '${options.method} ${options.uri.path}';
    sent.add((DateTime.now(), endpointOf(options)));
    final n = (_inFlight[key] ?? 0) + 1;
    _inFlight[key] = n;
    _total++;
    if (n > maxPerEndpoint) maxPerEndpoint = n;
    if (_total > maxTotal) maxTotal = _total;
    handler.next(options);
  }

  void _done(RequestOptions options) {
    final key = '${options.method} ${options.uri.path}';
    _inFlight[key] = (_inFlight[key] ?? 1) - 1;
    _total--;
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    _done(response.requestOptions);
    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    _done(err.requestOptions);
    failures++;
    if (err.response?.statusCode == 429) rateLimited++;
    handler.next(err);
  }

  /// Requests in the busiest sliding 60-second window.
  int busiestMinute({bool Function(String endpoint)? where}) {
    final times = [
      for (final (at, endpoint) in sent)
        if (where == null || where(endpoint)) at,
    ];
    var best = 0;
    var start = 0;
    for (var end = 0; end < times.length; end++) {
      while (times[end].difference(times[start]) >=
          const Duration(seconds: 60)) {
        start++;
      }
      if (end - start + 1 > best) best = end - start + 1;
    }
    return best;
  }
}
