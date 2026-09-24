import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

/// The operating system's "reduce motion" preference, applied in one place.
///
/// The application has little motion of its own: route transitions, dialog
/// fades, and the spinner of the live-status line. When the operator has
/// asked the platform for reduced motion (`MediaQuery.disableAnimations`),
/// the transitions and the decorative spinner are dropped. State changes —
/// the new page, the open dialog, the status text — still happen and are
/// still announced; only the animation between them goes.
class AppMotion {
  const AppMotion._();

  static bool reduced(BuildContext context) =>
      MediaQuery.maybeDisableAnimationsOf(context) ?? false;

  /// Route transition for `GetMaterialApp.customTransition`.
  static final CustomTransition pageTransition = _PageTransition();

  /// `showDialog(animationStyle: …)`: none when motion is reduced, the
  /// platform default otherwise.
  static AnimationStyle? dialogAnimation(BuildContext context) =>
      reduced(context) ? AnimationStyle.noAnimation : null;
}

/// The platform's own page transition, or none at all.
///
/// Without a custom transition GetX builds `const PageTransitionsTheme()`
/// directly, ignoring both the theme and the motion preference; this defers
/// to the theme's transition and skips it when motion is reduced.
class _PageTransition extends CustomTransition {
  @override
  Widget buildTransition(
    BuildContext context,
    Curve? curve,
    Alignment? alignment,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
    Widget child,
  ) {
    if (AppMotion.reduced(context)) return child;
    final route = ModalRoute.of(context);
    if (route is! PageRoute) return child;
    return Theme.of(context).pageTransitionsTheme
        .buildTransitions(route, context, animation, secondaryAnimation, child);
  }
}
