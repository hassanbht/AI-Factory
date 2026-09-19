import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/config/app_config.dart';
import '../../core/theme/app_spacing.dart';
import '../../core/theme/theme_service.dart';
import '../shell/app_shell.dart';
import '../shell/shell_destination.dart';
import '../widgets/language_selector.dart';

/// The one destination that is functional in Phase 19.1: it proves the
/// localization, theme and configuration foundations end to end.
class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  @override
  Widget build(BuildContext context) {
    final config = Get.find<AppConfig>();

    return AppShell(
      destination: ShellDestination.settings,
      builder: (context) => ListView(
        children: [
          _Section(
            title: 'settings.appearance.section'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: const [
                LanguageSelector(),
                SizedBox(height: AppSpacing.lg),
                _ThemeSelector(),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          _Section(
            title: 'settings.connection.section'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _InfoRow(
                  label: 'settings.connection.environment'.tr(),
                  value: config.environment.name,
                ),
                _InfoRow(
                  label: 'settings.connection.baseUrl'.tr(),
                  value: config.isConfigured
                      ? config.apiBaseUrl
                      : 'common.notAvailable'.tr(),
                ),
                const SizedBox(height: AppSpacing.sm),
                Text(
                  'settings.connection.hint'.tr(),
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          _Section(
            title: 'settings.about.section'.tr(),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _InfoRow(
                  label: 'settings.about.appVersion'.tr(),
                  value: AppConfig.clientVersion,
                ),
                _InfoRow(
                  label: 'settings.about.backendTarget'.tr(),
                  value: 'v${AppConfig.targetBackendVersion}',
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _ThemeSelector extends StatelessWidget {
  const _ThemeSelector();

  @override
  Widget build(BuildContext context) {
    final service = Get.find<ThemeService>();
    final theme = Theme.of(context);

    return Obx(() {
      final mode = service.modeListenable.value;

      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'settings.appearance.theme'.tr(),
            style: theme.textTheme.titleSmall,
          ),
          const SizedBox(height: AppSpacing.sm),
          Semantics(
            label: 'settings.appearance.theme'.tr(),
            child: SegmentedButton<ThemeMode>(
              segments: <ButtonSegment<ThemeMode>>[
                ButtonSegment<ThemeMode>(
                  value: ThemeMode.system,
                  icon: const Icon(Icons.brightness_auto_outlined),
                  label: Text('settings.appearance.themeSystem'.tr()),
                ),
                ButtonSegment<ThemeMode>(
                  value: ThemeMode.light,
                  icon: const Icon(Icons.light_mode_outlined),
                  label: Text('settings.appearance.themeLight'.tr()),
                ),
                ButtonSegment<ThemeMode>(
                  value: ThemeMode.dark,
                  icon: const Icon(Icons.dark_mode_outlined),
                  label: Text('settings.appearance.themeDark'.tr()),
                ),
              ],
              selected: <ThemeMode>{mode},
              showSelectedIcon: false,
              onSelectionChanged: (selection) => service.apply(selection.first),
            ),
          ),
        ],
      );
    });
  }
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.child});

  final String title;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: AppSpacing.md),
            child,
          ],
        ),
      ),
    );
  }
}

class _InfoRow extends StatelessWidget {
  const _InfoRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: AppSpacing.xs),
      child: Wrap(
        spacing: AppSpacing.sm,
        runSpacing: AppSpacing.xs,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(
            label,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          SelectableText(value, style: theme.textTheme.bodyMedium),
        ],
      ),
    );
  }
}
