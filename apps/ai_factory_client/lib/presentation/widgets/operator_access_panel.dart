import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/theme/app_spacing.dart';
import '../controllers/operator_access_controller.dart';
import 'panels.dart';

/// Enters, replaces and clears the operational API key.
///
/// The field is obscured, the value never leaves this widget except to
/// [OperatorAccessController.saveKey], and no error message ever quotes it.
/// The panel shows only whether a key is stored — never the key, not even
/// masked, because a masked secret is still a secret with a length.
///
/// It appears when a section answered `401`, and stays reachable afterwards
/// so a key can be replaced or removed without provoking another failure.
class OperatorAccessPanel extends StatefulWidget {
  const OperatorAccessPanel({
    required this.controller,
    this.onSaved,
    super.key,
  });

  final OperatorAccessController controller;

  /// Called after a successful save so the caller can retry what failed.
  final VoidCallback? onSaved;

  @override
  State<OperatorAccessPanel> createState() => _OperatorAccessPanelState();
}

class _OperatorAccessPanelState extends State<OperatorAccessPanel> {
  final TextEditingController _field = TextEditingController();
  bool _obscured = true;

  @override
  void dispose() {
    // The entered value dies with the widget rather than lingering in a
    // controller someone else could read.
    _field.clear();
    _field.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final saved = await widget.controller.saveKey(_field.text);
    if (!mounted) return;
    if (saved) {
      _field.clear();
      widget.onSaved?.call();
    }
  }

  Future<void> _clear() async {
    await widget.controller.clearKey();
    if (!mounted) return;
    _field.clear();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return SectionCard(
      title: 'operatorAccess.title'.tr(),
      subtitle: 'operatorAccess.subtitle'.tr(),
      child: Obx(() {
        final controller = widget.controller;
        final hasKey = controller.hasKey.value;
        final busy = controller.isBusy.value;
        final storageError = controller.storageError.value;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Semantics(
              liveRegion: true,
              child: Row(
                children: [
                  Icon(
                    hasKey ? Icons.lock_outline : Icons.lock_open_outlined,
                    size: 16,
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                  const SizedBox(width: AppSpacing.sm),
                  Text(
                    hasKey
                        ? 'operatorAccess.keyStored'.tr()
                        : 'operatorAccess.noKey'.tr(),
                    style: theme.textTheme.bodyMedium,
                  ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.md),
            TextField(
              controller: _field,
              obscureText: _obscured,
              enabled: !busy,
              autocorrect: false,
              enableSuggestions: false,
              onSubmitted: (_) => _save(),
              decoration: InputDecoration(
                labelText: 'operatorAccess.field'.tr(),
                helperText: 'operatorAccess.fieldHelp'.tr(),
                helperMaxLines: 3,
                border: const OutlineInputBorder(),
                suffixIcon: IconButton(
                  onPressed: () => setState(() => _obscured = !_obscured),
                  tooltip: _obscured
                      ? 'operatorAccess.show'.tr()
                      : 'operatorAccess.hide'.tr(),
                  icon: Icon(
                    _obscured
                        ? Icons.visibility_outlined
                        : Icons.visibility_off_outlined,
                  ),
                ),
              ),
            ),
            if (widget.controller.isSessionOnly) ...[
              const SizedBox(height: AppSpacing.sm),
              Text(
                'operatorAccess.sessionOnly'.tr(),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
            if (storageError != null) ...[
              const SizedBox(height: AppSpacing.sm),
              Text(
                'operatorAccess.storageFailed'.tr(),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.error,
                ),
              ),
            ],
            const SizedBox(height: AppSpacing.md),
            Wrap(
              spacing: AppSpacing.sm,
              runSpacing: AppSpacing.sm,
              children: [
                FilledButton(
                  onPressed: busy ? null : _save,
                  child: Text('operatorAccess.save'.tr()),
                ),
                if (hasKey)
                  OutlinedButton(
                    onPressed: busy ? null : _clear,
                    child: Text('operatorAccess.clear'.tr()),
                  ),
              ],
            ),
            const SizedBox(height: AppSpacing.md),
            UnavailableNote(
              title: 'operatorAccess.scope'.tr(),
              reason: 'operatorAccess.scopeNote'.tr(),
            ),
          ],
        );
      }),
    );
  }
}
