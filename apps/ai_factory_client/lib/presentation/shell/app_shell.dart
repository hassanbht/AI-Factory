import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/responsive/breakpoints.dart';
import '../../core/theme/app_spacing.dart';
import 'shell_destination.dart';

/// The adaptive application frame every screen is rendered inside.
///
/// One widget tree for Windows, Web and tablet:
///
/// * expanded — navigation rail with visible labels,
/// * medium — icon-only rail with tooltips,
/// * compact — bottom navigation bar.
///
/// Labels come from translation keys, so a language change updates the whole
/// frame without rebuilding the route.
class AppShell extends StatelessWidget {
  const AppShell({
    required this.destination,
    required this.builder,
    this.actions,
    super.key,
  });

  final ShellDestination destination;

  /// The page content, supplied as a builder rather than a widget.
  ///
  /// A language change rebuilds this shell, which then calls the builder
  /// again — so the page below it re-reads its translations too. Passing a
  /// prebuilt widget would let Flutter skip the subtree, leaving the page in
  /// the previous language.
  final WidgetBuilder builder;

  final List<Widget>? actions;

  void _onSelect(ShellDestination target) {
    if (target == destination) return;
    Get.offNamed<void>(target.route);
  }

  @override
  Widget build(BuildContext context) {
    // Registers a dependency on the active locale: `.tr()` reads a static
    // translation table, so without this the shell would never be rebuilt
    // when the operator switches language.
    final _ = context.locale;

    return ResponsiveBuilder(
      builder: (context, sizeClass) {
        final body = _ShellBody(child: Builder(builder: builder));

        return Scaffold(
          appBar: AppBar(
            title: Text(destination.labelKey.tr()),
            actions: actions,
          ),
          body: sizeClass.usesNavigationRail
              ? Row(
                  children: [
                    _Rail(
                      destination: destination,
                      extended: sizeClass.usesExtendedRail,
                      onSelect: _onSelect,
                    ),
                    const VerticalDivider(width: 1),
                    Expanded(child: body),
                  ],
                )
              : body,
          bottomNavigationBar: sizeClass.usesNavigationRail
              ? null
              : _BottomBar(destination: destination, onSelect: _onSelect),
        );
      },
    );
  }
}

class _ShellBody extends StatelessWidget {
  const _ShellBody({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(
            maxWidth: AppSpacing.maxContentWidth,
          ),
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.pageGutter),
            child: child,
          ),
        ),
      ),
    );
  }
}

class _Rail extends StatelessWidget {
  const _Rail({
    required this.destination,
    required this.extended,
    required this.onSelect,
  });

  final ShellDestination destination;
  final bool extended;
  final void Function(ShellDestination) onSelect;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: 'nav.mainNavigation'.tr(),
      container: true,
      child: NavigationRail(
        selectedIndex: destination.index,
        extended: extended,
        // German labels are noticeably longer than the English ones, so the
        // extended rail is given room rather than being clipped.
        minExtendedWidth: 208,
        labelType: extended ? null : NavigationRailLabelType.none,
        onDestinationSelected: (index) =>
            onSelect(ShellDestination.values[index]),
        destinations: [
          for (final item in ShellDestination.values)
            NavigationRailDestination(
              icon: Tooltip(
                message: item.labelKey.tr(),
                child: Icon(item.icon),
              ),
              selectedIcon: Tooltip(
                message: item.labelKey.tr(),
                child: Icon(item.selectedIcon),
              ),
              label: Text(item.labelKey.tr()),
            ),
        ],
      ),
    );
  }
}

class _BottomBar extends StatelessWidget {
  const _BottomBar({required this.destination, required this.onSelect});

  final ShellDestination destination;
  final void Function(ShellDestination) onSelect;

  @override
  Widget build(BuildContext context) {
    return NavigationBar(
      selectedIndex: destination.index,
      onDestinationSelected: (index) =>
          onSelect(ShellDestination.values[index]),
      destinations: [
        for (final item in ShellDestination.values)
          NavigationDestination(
            icon: Icon(item.icon),
            selectedIcon: Icon(item.selectedIcon),
            label: item.labelKey.tr(),
            tooltip: item.labelKey.tr(),
          ),
      ],
    );
  }
}
