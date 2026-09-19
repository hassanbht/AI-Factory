import 'package:flutter/widgets.dart';

/// Material 3 window size classes, the breakpoint model for every screen.
///
/// One widget tree adapts across all of them. There is no separate Windows
/// tree and no separate Web tree.
enum WindowSizeClass {
  /// Phone width and narrow web windows: single column, drawer navigation.
  compact,

  /// Tablet width: navigation rail, two columns where a screen has a
  /// master/detail shape.
  medium,

  /// Desktop, the primary target: navigation rail plus multi-column content.
  expanded;

  static const double mediumMinWidth = 600;
  static const double expandedMinWidth = 1024;

  static WindowSizeClass fromWidth(double width) {
    if (width >= expandedMinWidth) return WindowSizeClass.expanded;
    if (width >= mediumMinWidth) return WindowSizeClass.medium;
    return WindowSizeClass.compact;
  }

  static WindowSizeClass of(BuildContext context) =>
      fromWidth(MediaQuery.sizeOf(context).width);

  bool get isCompact => this == WindowSizeClass.compact;

  bool get isMedium => this == WindowSizeClass.medium;

  bool get isExpanded => this == WindowSizeClass.expanded;

  /// A rail is used from tablet width upwards; below that navigation lives in
  /// a drawer.
  bool get usesNavigationRail => this != WindowSizeClass.compact;

  /// Rail labels are only shown when there is room for German ones, which are
  /// noticeably longer than the English equivalents.
  bool get usesExtendedRail => this == WindowSizeClass.expanded;
}

/// Rebuilds its child when the window size class changes.
class ResponsiveBuilder extends StatelessWidget {
  const ResponsiveBuilder({required this.builder, super.key});

  final Widget Function(BuildContext context, WindowSizeClass sizeClass)
  builder;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) =>
          builder(context, WindowSizeClass.fromWidth(constraints.maxWidth)),
    );
  }
}
