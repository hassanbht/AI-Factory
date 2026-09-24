import 'dart:math' as math;

import 'package:ai_factory_client/core/theme/app_theme.dart';
import 'package:ai_factory_client/presentation/widgets/status_chip.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/screen_catalog.dart';
import '../support/test_app.dart';

/// Foreground/background contrast, measured.
///
/// Two layers:
///
/// 1. The colour pairs the application actually uses, computed with the WCAG
///    2.x relative-luminance formula for both themes: body and secondary
///    text, every status tone (text on its own tinted chip, on every surface
///    a chip is placed on), errors, warnings, selected controls and the focus
///    ring. Text needs 4.5:1 (all status labels are small text); the focus
///    ring and other non-text indicators need 3:1.
/// 2. Flutter's own `textContrastGuideline` over the real rendered screens,
///    which samples the pixels behind every visible text node.
///
/// Disabled controls are exempt under WCAG 1.4.3 ("inactive user interface
/// components") and are not asserted; they also carry no state on their own
/// — every disabled action has an enabled-state label or a reason beside it.
void main() {
  for (final theme in <String, ThemeData>{
    'light': AppTheme.light(),
    'dark': AppTheme.dark(),
  }.entries) {
    final scheme = theme.value.colorScheme;
    final surfaces = <String, Color>{
      'surface': scheme.surface,
      'surfaceContainerLowest': scheme.surfaceContainerLowest,
      'surfaceContainerLow': scheme.surfaceContainerLow,
      'surfaceContainer': scheme.surfaceContainer,
      'surfaceContainerHigh': scheme.surfaceContainerHigh,
    };

    group('${theme.key} theme', () {
      test('body and secondary text', () {
        for (final surface in surfaces.entries) {
          expectContrast(scheme.onSurface, surface.value, 4.5, surface.key);
          expectContrast(
            scheme.onSurfaceVariant,
            surface.value,
            4.5,
            'onSurfaceVariant on ${surface.key}',
          );
        }
      });

      test('every status tone, as text on its tinted chip, on every '
          'surface', () {
        final failures = <String>[];
        for (final tone in StatusTone.values) {
          final foreground = tone.color(scheme);
          for (final surface in surfaces.entries) {
            // StatusChip fills with the tone at 12 % over whatever it sits on.
            final chip = Color.alphaBlend(
              foreground.withValues(alpha: 0.12),
              surface.value,
            );
            final ratio = contrastRatio(foreground, chip);
            if (ratio < 4.5) {
              failures.add(
                '${tone.name} on ${surface.key}: '
                '${ratio.toStringAsFixed(2)}:1',
              );
            }
          }
        }
        expect(failures, isEmpty);
      });

      test('errors and warnings', () {
        for (final surface in surfaces.entries) {
          expectContrast(scheme.error, surface.value, 4.5, surface.key);
        }
        expectContrast(
          scheme.onErrorContainer,
          scheme.errorContainer,
          4.5,
          'error banner',
        );
        expectContrast(
          StatusPalette.warning(scheme),
          scheme.surface,
          4.5,
          'warning text',
        );
      });

      test('selected controls', () {
        expectContrast(
          scheme.onSecondaryContainer,
          scheme.secondaryContainer,
          4.5,
          'navigation indicator',
        );
        expectContrast(scheme.onPrimary, scheme.primary, 4.5, 'filled button');
        // A selected work item / agent run row: 35 % primary container.
        final selectedRow = Color.alphaBlend(
          scheme.primaryContainer.withValues(alpha: 0.35),
          scheme.surfaceContainerLow,
        );
        expectContrast(scheme.onSurface, selectedRow, 4.5, 'selected row');
        expectContrast(
          scheme.onSurfaceVariant,
          selectedRow,
          4.5,
          'selected row, secondary text',
        );
      });

      test('the focus ring stands out from every surface (3:1)', () {
        for (final surface in surfaces.entries) {
          expectContrast(scheme.primary, surface.value, 3, surface.key);
        }
      });
    });
  }

  group('rendered screens meet the text contrast guideline', () {
    setUpAll(initializeTestLocalization);

    const screens = <CatalogScreen>[
      CatalogScreen.dashboard,
      CatalogScreen.projects,
      CatalogScreen.projectDetail,
      CatalogScreen.agentRuns,
      CatalogScreen.humanReview,
      CatalogScreen.operations,
      CatalogScreen.diagnostics,
      CatalogScreen.settings,
    ];

    for (final screen in screens) {
      for (final dark in const [false, true]) {
        testWidgets('${screen.name}, ${dark ? 'dark' : 'light'}', (
          tester,
        ) async {
          pinCatalogClock();
          await registerTestServices();
          registerCatalog(screen);
          await pumpCatalogScreen(
            tester,
            screen,
            size: const Size(1280, 900),
            theme: dark ? AppTheme.dark() : AppTheme.light(),
          );
          await expectLater(tester, meetsGuideline(textContrastGuideline));
        });
      }
    }
  });
}

void expectContrast(
  Color foreground,
  Color background,
  double minimum,
  String what,
) {
  final ratio = contrastRatio(foreground, background);
  expect(
    ratio,
    greaterThanOrEqualTo(minimum),
    reason: '$what: ${ratio.toStringAsFixed(2)}:1 is below $minimum:1',
  );
}

/// WCAG 2.x contrast ratio.
double contrastRatio(Color a, Color b) {
  final la = _luminance(a);
  final lb = _luminance(b);
  return (math.max(la, lb) + 0.05) / (math.min(la, lb) + 0.05);
}

double _luminance(Color color) {
  double channel(double c) =>
      c <= 0.03928 ? c / 12.92 : math.pow((c + 0.055) / 1.055, 2.4).toDouble();
  return 0.2126 * channel(color.r) +
      0.7152 * channel(color.g) +
      0.0722 * channel(color.b);
}
