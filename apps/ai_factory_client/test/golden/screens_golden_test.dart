@Tags(<String>['golden'])
library;

import 'package:ai_factory_client/core/theme/app_theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/screen_catalog.dart';
import '../support/test_app.dart';
import 'golden_support.dart';

/// Visual goldens of the real application screens.
///
/// Every screen renders through its production route with the shared
/// [ScreenCatalog] data, the clock and display zone pinned, a device pixel
/// ratio of 1 and a fixed window, so a capture changes only when the UI does.
///
/// Matrix (see `docs/flutter-testing.md`):
///
/// * light theme — all ten screens × English/German × compact/medium/expanded;
/// * dark theme — Dashboard, Project Detail, WorkItems, AgentRuns, Human Review
///   and Operations × English/German × compact/medium/expanded.
///
/// Regenerate after an intentional UI change, on Windows (the goldens are
/// rendered by the Windows test engine):
///
///     flutter test --update-goldens test/golden
void main() {
  setUpAll(() async {
    await initializeTestLocalization();
    await loadGoldenFonts();
  });

  const locales = <String, Locale>{'en': Locale('en'), 'de': Locale('de')};

  const darkScreens = <CatalogScreen>{
    CatalogScreen.dashboard,
    CatalogScreen.projectDetail,
    CatalogScreen.workItems,
    CatalogScreen.agentRuns,
    CatalogScreen.humanReview,
    CatalogScreen.operations,
  };

  for (final screen in CatalogScreen.values) {
    for (final brightness in Brightness.values) {
      if (brightness == Brightness.dark && !darkScreens.contains(screen)) {
        continue;
      }
      for (final locale in locales.entries) {
        for (final size in GoldenSize.values) {
          final name =
              '${screen.name}_${locale.key}_${size.name}_${brightness.name}';

          testWidgets(name, (tester) async {
            pinCatalogClock();
            tester.view.devicePixelRatio = 1;
            addTearDown(tester.view.resetDevicePixelRatio);

            await registerTestServices();
            registerCatalog(screen);
            await pumpCatalogScreen(
              tester,
              screen,
              size: size.of(screen),
              locale: locale.value,
              theme: brightness == Brightness.dark
                  ? AppTheme.dark()
                  : AppTheme.light(),
            );

            expectNoUntranslatedKeys(tester);
            await expectLater(
              find.byType(MaterialApp),
              matchesGoldenFile('goldens/$name.png'),
            );
          });
        }
      }
    }
  }
}

/// The three breakpoints, each at a real window size. Long screens get a
/// taller window so the capture holds every section rather than the top.
enum GoldenSize {
  compact(Size(400, 900), Size(400, 2400)),
  medium(Size(800, 900), Size(800, 2000)),
  expanded(Size(1280, 900), Size(1280, 1600));

  const GoldenSize(this.window, this.tall);

  final Size window;
  final Size tall;

  Size of(CatalogScreen screen) => screen.isTall ? tall : window;
}
