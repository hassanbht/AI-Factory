import 'package:flutter/material.dart';

import '../../app/app_routes.dart';

/// The top-level navigation destinations of the operator console.
///
/// [labelKey] is a translation key, never a literal label: the rail, the
/// bottom bar and the app bar all read the same key, so switching language
/// updates every one of them at once.
enum ShellDestination {
  dashboard(
    route: AppRoutes.dashboard,
    labelKey: 'nav.dashboard',
    icon: Icons.space_dashboard_outlined,
    selectedIcon: Icons.space_dashboard,
  ),
  projects(
    route: AppRoutes.projects,
    labelKey: 'nav.projects',
    icon: Icons.folder_outlined,
    selectedIcon: Icons.folder,
  ),
  operations(
    route: AppRoutes.operations,
    labelKey: 'nav.operations',
    icon: Icons.monitor_heart_outlined,
    selectedIcon: Icons.monitor_heart,
  ),
  settings(
    route: AppRoutes.settings,
    labelKey: 'nav.settings',
    icon: Icons.settings_outlined,
    selectedIcon: Icons.settings,
  );

  const ShellDestination({
    required this.route,
    required this.labelKey,
    required this.icon,
    required this.selectedIcon,
  });

  final String route;
  final String labelKey;
  final IconData icon;
  final IconData selectedIcon;
}
