import 'package:ai_factory_client/app/app_bindings.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/test_app.dart';

/// How a controller test substitutes a fake repository — pinned in its own
/// file because it only holds in a container where the bindings have not run
/// yet.
///
/// `AppBindings` registers the repositories with `lazyPut(fenix: true)`, and a
/// fenix builder survives `Get.deleteAll(force: true)`: once the bindings have
/// run in a test process, later `put`, `replace` and `lazyReplace` calls for
/// those types are all defeated by the surviving builder. A test that needs a
/// fake therefore registers it **before** the bindings run, which `lazyPut`
/// then leaves alone.
void main() {
  setUpAll(initializeTestLocalization);

  setUp(registerTestServices);

  tearDown(() async => Get.deleteAll(force: true));

  test('a fake registered before the bindings run is the one resolved', () {
    Get.put<ProjectRepository>(_FakeProjectRepository(), permanent: true);

    AppBindings().dependencies();

    expect(Get.find<ProjectRepository>(), isA<_FakeProjectRepository>());
    // Everything the test did not stub is still the real wiring.
    expect(Get.find<WorkItemRepository>(), isA<WorkItemRepository>());
    expect(Get.find<ApprovalRepository>(), isA<ApprovalRepository>());
  });
}

class _FakeProjectRepository implements ProjectRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName} is not faked');
}
