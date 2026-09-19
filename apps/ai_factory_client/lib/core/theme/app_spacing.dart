/// Spacing and radius scale. Every layout uses these instead of ad-hoc
/// numbers, so density stays consistent as screens are added.
class AppSpacing {
  const AppSpacing._();

  static const double xs = 4;
  static const double sm = 8;
  static const double md = 16;
  static const double lg = 24;
  static const double xl = 32;
  static const double xxl = 48;

  /// Side gutter for page content.
  static const double pageGutter = md;

  /// Content never grows wider than this, so text stays readable on a very
  /// wide desktop display.
  static const double maxContentWidth = 1440;
}

class AppRadius {
  const AppRadius._();

  static const double sm = 6;
  static const double md = 12;
  static const double lg = 20;
}
