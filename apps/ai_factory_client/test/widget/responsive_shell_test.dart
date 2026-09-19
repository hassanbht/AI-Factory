import 'package:ai_factory_client/app/app.dart';
import 'package:ai_factory_client/core/responsive/breakpoints.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/test_app.dart';

/// The shell must adapt from a narrow window to a desktop one without a
/// second widget tree.
void main() {
  setUpAll(initializeTestLocalization);

  setUp(registerTestServices);

  group('WindowSizeClass', () {
    test('classifies widths against the Material 3 breakpoints', () {
      expect(WindowSizeClass.fromWidth(400), WindowSizeClass.compact);
      expect(WindowSizeClass.fromWidth(599), WindowSizeClass.compact);
      expect(WindowSizeClass.fromWidth(600), WindowSizeClass.medium);
      expect(WindowSizeClass.fromWidth(1023), WindowSizeClass.medium);
      expect(WindowSizeClass.fromWidth(1024), WindowSizeClass.expanded);
      expect(WindowSizeClass.fromWidth(1920), WindowSizeClass.expanded);
    });

    test('only compact uses bottom navigation', () {
      expect(WindowSizeClass.compact.usesNavigationRail, isFalse);
      expect(WindowSizeClass.medium.usesNavigationRail, isTrue);
      expect(WindowSizeClass.expanded.usesNavigationRail, isTrue);
    });

    test('rail labels are only shown at desktop width', () {
      expect(WindowSizeClass.compact.usesExtendedRail, isFalse);
      expect(WindowSizeClass.medium.usesExtendedRail, isFalse);
      expect(WindowSizeClass.expanded.usesExtendedRail, isTrue);
    });
  });

  testWidgets('compact width uses a bottom navigation bar', (tester) async {
    await setSurfaceSize(tester, TestSizes.compact);
    await tester.pumpWidget(
      const AiFactoryAppRoot(assetLoader: SyncFileAssetLoader()),
    );
    await tester.pumpAndSettle();

    expect(find.byType(NavigationBar), findsOneWidget);
    expect(find.byType(NavigationRail), findsNothing);
  });

  testWidgets('desktop width uses an extended navigation rail', (tester) async {
    await setSurfaceSize(tester, TestSizes.expanded);
    await tester.pumpWidget(
      const AiFactoryAppRoot(assetLoader: SyncFileAssetLoader()),
    );
    await tester.pumpAndSettle();

    expect(find.byType(NavigationRail), findsOneWidget);
    expect(find.byType(NavigationBar), findsNothing);
    expect(
      tester.widget<NavigationRail>(find.byType(NavigationRail)).extended,
      isTrue,
    );
  });

  testWidgets('tablet width uses an icon-only rail', (tester) async {
    await setSurfaceSize(tester, TestSizes.medium);
    await tester.pumpWidget(
      const AiFactoryAppRoot(assetLoader: SyncFileAssetLoader()),
    );
    await tester.pumpAndSettle();

    expect(find.byType(NavigationRail), findsOneWidget);
    expect(
      tester.widget<NavigationRail>(find.byType(NavigationRail)).extended,
      isFalse,
    );
  });
}
