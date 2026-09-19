import 'package:flutter/material.dart';

import 'app_spacing.dart';

/// Material 3 light and dark themes.
///
/// This is a consistency foundation, not final branding: colours come from a
/// single seed, and the status palette is defined once so later phases render
/// project/work-item/agent states identically everywhere.
class AppTheme {
  const AppTheme._();

  static const Color _seed = Color(0xFF3B5BDB);

  static ThemeData light() => _build(Brightness.light);

  static ThemeData dark() => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final colorScheme = ColorScheme.fromSeed(
      seedColor: _seed,
      brightness: brightness,
    );

    final base = ThemeData(
      colorScheme: colorScheme,
      useMaterial3: true,
      visualDensity: VisualDensity.standard,
    );

    return base.copyWith(
      scaffoldBackgroundColor: colorScheme.surface,
      appBarTheme: AppBarTheme(
        backgroundColor: colorScheme.surface,
        surfaceTintColor: colorScheme.surfaceTint,
        centerTitle: false,
        titleTextStyle: base.textTheme.titleLarge,
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppRadius.md),
          side: BorderSide(color: colorScheme.outlineVariant),
        ),
      ),
      chipTheme: base.chipTheme.copyWith(
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppRadius.sm),
        ),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          // Keyboard and pointer targets stay comfortably large on desktop.
          minimumSize: const Size(0, 44),
          padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size(0, 44),
          padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
      ),
      navigationRailTheme: NavigationRailThemeData(
        backgroundColor: colorScheme.surfaceContainerLow,
        indicatorColor: colorScheme.secondaryContainer,
        labelType: NavigationRailLabelType.none,
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: colorScheme.surfaceContainerLow,
        indicatorColor: colorScheme.secondaryContainer,
      ),
      dividerTheme: DividerThemeData(
        color: colorScheme.outlineVariant,
        space: 1,
        thickness: 1,
      ),
      listTileTheme: const ListTileThemeData(
        contentPadding: EdgeInsets.symmetric(horizontal: AppSpacing.md),
      ),
    );
  }
}

/// Semantic colours for backend states.
///
/// Colour alone never carries meaning in this application — every status is
/// rendered with an icon and a localized label as well — but a consistent
/// palette makes a long list scannable.
class StatusPalette {
  const StatusPalette._();

  static Color neutral(ColorScheme scheme) => scheme.outline;

  static Color active(ColorScheme scheme) => scheme.primary;

  static Color success(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF2F7D32)
      : const Color(0xFF81C995);

  static Color warning(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF9A6700)
      : const Color(0xFFE3B341);

  static Color danger(ColorScheme scheme) => scheme.error;

  /// Awaiting a human decision — deliberately distinct from both "running"
  /// and "done", because it is the one state that needs a person.
  static Color attention(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF6D28D9)
      : const Color(0xFFC4B5FD);
}
