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

    // Keyboard focus is drawn as a 2 dp outline in the primary colour. The
    // Material 3 default is a faint overlay tint, too weak to follow on a
    // dense desktop screen; unfocused states fall through to the defaults.
    final focusRing = WidgetStateProperty.resolveWith<BorderSide?>(
      (states) => states.contains(WidgetState.focused)
          ? BorderSide(color: colorScheme.primary, width: 2)
          : null,
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
        ).copyWith(side: focusRing),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size(0, 44),
          padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
        ).copyWith(side: focusRing),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(minimumSize: const Size(0, 44))
            .copyWith(side: focusRing),
      ),
      iconButtonTheme: IconButtonThemeData(style: ButtonStyle(side: focusRing)),
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

  /// `onSurfaceVariant`, not `outline`: a neutral chip's label is small text,
  /// and `outline` reaches only ~3.7:1 on its own tint (WCAG needs 4.5:1).
  static Color neutral(ColorScheme scheme) => scheme.onSurfaceVariant;

  static Color active(ColorScheme scheme) => scheme.primary;

  static Color success(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF1E6B23)
      : const Color(0xFF81C995);

  static Color warning(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF7A5100)
      : const Color(0xFFE3B341);

  /// In the light theme `error` misses 4.5:1 on the darker containers once
  /// the chip's own tint is added, so the deeper `onErrorContainer` red is
  /// used there.
  static Color danger(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? scheme.onErrorContainer
      : scheme.error;

  /// Awaiting a human decision — deliberately distinct from both "running"
  /// and "done", because it is the one state that needs a person.
  static Color attention(ColorScheme scheme) =>
      scheme.brightness == Brightness.light
      ? const Color(0xFF6D28D9)
      : const Color(0xFFC4B5FD);
}
