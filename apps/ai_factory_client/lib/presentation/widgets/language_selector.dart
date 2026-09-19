import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/localization/locale_service.dart';
import '../../core/theme/app_spacing.dart';

/// Language selector: System default / English / Deutsch.
///
/// Switching is immediate — `easy_localization` rebuilds the tree, so no
/// restart is involved and no screen state is lost. The explicit choice is
/// persisted and survives a restart; "System default" clears the saved locale
/// and follows the operating system again.
class LanguageSelector extends StatelessWidget {
  const LanguageSelector({super.key});

  @override
  Widget build(BuildContext context) {
    final service = Get.find<LocaleService>();
    final theme = Theme.of(context);

    return Obx(() {
      final preference = service.preferenceListenable.value;
      final effective = LocaleService.resolve(preference, context.deviceLocale);

      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'settings.appearance.language'.tr(),
            style: theme.textTheme.titleSmall,
          ),
          const SizedBox(height: AppSpacing.xs),
          Text(
            'settings.appearance.languageHint'.tr(),
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          Semantics(
            label: 'settings.appearance.language'.tr(),
            child: SegmentedButton<LocalePreference>(
              segments: <ButtonSegment<LocalePreference>>[
                ButtonSegment<LocalePreference>(
                  value: LocalePreference.system,
                  icon: const Icon(Icons.devices_outlined),
                  label: Text('settings.appearance.languageSystem'.tr()),
                ),
                ButtonSegment<LocalePreference>(
                  value: LocalePreference.english,
                  label: Text('settings.appearance.languageEnglish'.tr()),
                ),
                ButtonSegment<LocalePreference>(
                  value: LocalePreference.german,
                  label: Text('settings.appearance.languageGerman'.tr()),
                ),
              ],
              selected: <LocalePreference>{preference},
              showSelectedIcon: false,
              onSelectionChanged: (selection) =>
                  service.apply(context, selection.first),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          Text(
            'settings.appearance.activeLanguage'.tr(
              args: [effective.languageCode.toUpperCase()],
            ),
            style: theme.textTheme.bodySmall,
          ),
          if (preference != LocalePreference.system) ...[
            const SizedBox(height: AppSpacing.xs),
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: TextButton.icon(
                onPressed: () => service.resetToSystem(context),
                icon: const Icon(Icons.restart_alt),
                label: Text('settings.appearance.resetToSystem'.tr()),
              ),
            ),
          ],
        ],
      );
    });
  }
}
