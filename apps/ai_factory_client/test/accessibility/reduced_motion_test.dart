import 'dart:async';

import 'package:ai_factory_client/app/app_routes.dart';
import 'package:ai_factory_client/domain/repositories/repositories.dart';
import 'package:ai_factory_client/presentation/pages/project_detail_page.dart';
import 'package:ai_factory_client/presentation/widgets/live_updates.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:get/get.dart' hide Trans;

import '../support/fake_repositories.dart';
import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// The platform's "reduce motion" preference (`AccessibilityFeatures
/// .disableAnimations`, surfaced as `MediaQuery.disableAnimations`).
///
/// The application's motion is route transitions, dialog fades and the
/// live-status spinner. With the preference on, all three stop animating —
/// and the state they accompany still changes and is still announced.
void main() {
  setUpAll(initializeTestLocalization);

  const window = Size(1280, 900);

  setUp(() async {
    pinCatalogClock();
    await registerTestServices();
  });

  void reduceMotion(WidgetTester tester) {
    tester.platformDispatcher.accessibilityFeaturesTestValue =
        const FakeAccessibilityFeatures(disableAnimations: true);
    addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
  }

  /// Transition widgets between the app root and [page].
  List<Widget> transitionsAbove(WidgetTester tester, Finder page) => tester
      .widgetList(
        find.ancestor(
          of: page,
          matching: find.byWidgetPredicate(
            (w) =>
                w is FadeTransition ||
                w is ScaleTransition ||
                w is SlideTransition,
          ),
        ),
      )
      .toList();

  Future<void> openDetail(WidgetTester tester) async {
    await tester.tap(find.text('Customer API').first);
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 16));
  }

  group('route transitions', () {
    testWidgets('animate by default', (tester) async {
      registerCatalog(CatalogScreen.projects);
      await pumpCatalogScreen(tester, CatalogScreen.projects, size: window);

      await openDetail(tester);
      expect(
        transitionsAbove(tester, find.byType(ProjectDetailPage)),
        isNotEmpty,
      );
      await tester.pumpAndSettle();
    });

    testWidgets('are skipped when motion is reduced — the page still opens', (
      tester,
    ) async {
      reduceMotion(tester);
      registerCatalog(CatalogScreen.projects);
      await pumpCatalogScreen(tester, CatalogScreen.projects, size: window);

      await openDetail(tester);
      expect(find.byType(ProjectDetailPage), findsOneWidget);
      expect(transitionsAbove(tester, find.byType(ProjectDetailPage)), isEmpty);
      await tester.pumpAndSettle();
    });
  });

  group('dialogs', () {
    Future<List<double>> opacitiesRightAfterOpening(WidgetTester tester) async {
      registerCatalog(CatalogScreen.humanReview);
      await pumpCatalogScreen(tester, CatalogScreen.humanReview, size: window);
      await tester.tap(find.text('humanReview.approveButton'.tr()));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 16));
      return tester
          .widgetList<FadeTransition>(
            find.ancestor(
              of: find.byType(AlertDialog),
              matching: find.byType(FadeTransition),
            ),
          )
          .map((f) => f.opacity.value)
          .toList();
    }

    testWidgets('fade in by default', (tester) async {
      final opacities = await opacitiesRightAfterOpening(tester);
      expect(opacities.any((o) => o < 1), isTrue);
      await tester.pumpAndSettle();
    });

    testWidgets('appear at once when motion is reduced', (tester) async {
      reduceMotion(tester);
      final opacities = await opacitiesRightAfterOpening(tester);
      expect(find.byType(AlertDialog), findsOneWidget);
      expect(opacities.every((o) => o == 1), isTrue, reason: '$opacities');
      await tester.pumpAndSettle();
    });
  });

  group('live-status indicator', () {
    Future<Completer<void>> openWhileRefreshing(WidgetTester tester) async {
      final gate = Completer<void>();
      registerCatalog(CatalogScreen.dashboard);
      Get.delete<ProjectRepository>(force: true);
      Get.put<ProjectRepository>(
        FakeProjectRepository(projects: ScreenCatalog.data.projects())
          ..gate = gate.future,
      );
      Get.testMode = true;
      await setSurfaceSize(tester, window);
      await tester.pumpWidget(
        TestAppScope(
          initialRoute: CatalogScreen.dashboard.route,
          getPages: AppRoutes.pages,
        ),
      );
      await tester.pump();
      await tester.pump();
      return gate;
    }

    Finder spinner() => find.descendant(
      of: find.byType(LiveStatusIndicator),
      matching: find.byType(CircularProgressIndicator),
    );

    testWidgets('spins while a cycle is in flight by default', (tester) async {
      final gate = await openWhileRefreshing(tester);
      expect(spinner(), findsOneWidget);
      gate.complete();
      await tester.pumpAndSettle();
    });

    testWidgets('shows a still icon when motion is reduced, and still says '
        'it is refreshing', (tester) async {
      reduceMotion(tester);
      final gate = await openWhileRefreshing(tester);
      expect(spinner(), findsNothing);
      expect(
        find.descendant(
          of: find.byType(LiveStatusIndicator),
          matching: find.byIcon(Icons.sync),
        ),
        findsOneWidget,
      );
      expect(find.text('live.status.refreshing'.tr()), findsOneWidget);
      gate.complete();
      await tester.pumpAndSettle();
    });
  });
}
