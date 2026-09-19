import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_spacing.dart';

/// Honest placeholder for a destination whose functionality belongs to a later
/// phase. It says what will live here and that it does not yet — it never
/// mimics a working screen.
class PhasePlaceholder extends StatelessWidget {
  const PhasePlaceholder({
    required this.descriptionKey,
    required this.icon,
    super.key,
  });

  final String descriptionKey;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 520),
        child: Card(
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.lg),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(icon, size: 40, color: theme.colorScheme.primary),
                const SizedBox(height: AppSpacing.md),
                Text(
                  'placeholder.comingSoon'.tr(),
                  style: theme.textTheme.labelLarge?.copyWith(
                    color: theme.colorScheme.primary,
                  ),
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: AppSpacing.sm),
                Text(
                  descriptionKey.tr(),
                  style: theme.textTheme.bodyMedium,
                  textAlign: TextAlign.center,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
