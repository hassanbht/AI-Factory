import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/theme/app_motion.dart';

/// Shown before the approval POST is sent.
///
/// Two rules from Workstream K:
///
/// 1. **No ambiguous button.** The confirmatory action is labelled
///    `humanReview.confirm.approve` ("Approve"), never "OK", "Yes" or
///    "Continue", so the operator knows exactly what they are committing to.
///
/// 2. **Cancel is the keyboard default focus.** Approval is a consequential,
///    irreversible operation. Defaulting to the safe choice means an accidental
///    Enter key does not trigger it.
///
/// Returns `true` when the operator confirms, `false` (or `null`) otherwise.
class ApprovalConfirmationDialog extends StatelessWidget {
  const ApprovalConfirmationDialog({required this.projectName, super.key});

  final String projectName;

  /// Convenience: shows the dialog and returns whether the operator confirmed.
  static Future<bool> show(BuildContext context, String projectName) async {
    final result = await showDialog<bool>(
      context: context,
      barrierDismissible: false, // force an explicit choice
      animationStyle: AppMotion.dialogAnimation(context),
      builder: (_) => ApprovalConfirmationDialog(projectName: projectName),
    );
    return result == true;
  }

  @override
  Widget build(BuildContext context) {
    // Escape means Cancel. The barrier stays non-dismissible — a click
    // outside is not a decision — but the keyboard's own "back out" key is
    // the safe choice, never the approval.
    return Actions(
      actions: <Type, Action<Intent>>{
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            Navigator.of(context).pop(false);
            return null;
          },
        ),
      },
      child: AlertDialog(
        title: Text('humanReview.confirm.title'.tr()),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // The project name is operator data — never translated, never
            // abbreviated. It is the clearest identifier available here.
            Text(projectName, style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 8),
            Text('humanReview.confirm.body'.tr()),
          ],
        ),
        // Cancel is the first (and default-focus) button: a consequential
        // dialog's safe path must be the easiest one.
        actions: [
          TextButton(
            autofocus: true,
            onPressed: () => Navigator.of(context).pop(false),
            child: Text('humanReview.confirm.cancel'.tr()),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Semantics(
              label: 'humanReview.approveButtonSemantics'.tr(),
              excludeSemantics: true,
              child: Text('humanReview.confirm.approve'.tr()),
            ),
          ),
        ],
      ),
    );
  }
}
