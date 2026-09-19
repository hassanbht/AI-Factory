import 'package:flutter/material.dart';

import '../shell/app_shell.dart';
import '../shell/shell_destination.dart';
import '../widgets/phase_placeholder.dart';

/// Shell destination only. Operations functionality arrives in Phase 19.8.
class OperationsPage extends StatelessWidget {
  const OperationsPage({super.key});

  @override
  Widget build(BuildContext context) {
    return AppShell(
      destination: ShellDestination.operations,
      builder: (context) => const PhasePlaceholder(
        descriptionKey: 'placeholder.operations',
        icon: Icons.monitor_heart_outlined,
      ),
    );
  }
}
