import 'package:ai_factory_client/core/localization/enum_labels.dart';
import 'package:ai_factory_client/domain/enums/backend_enums.dart';
import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../support/test_app.dart';

/// Every backend state a user can see must have a translated label in both
/// languages, and the raw wire value must never be what is displayed.
void main() {
  setUpAll(initializeTestLocalization);

  /// One localization scope per test; the language is changed at runtime
  /// through the package's own API, exactly as the application does it.
  Future<void> pumpScope(WidgetTester tester) async {
    await tester.pumpWidget(
      const TestLocalizationScope(child: SizedBox.shrink()),
    );
    await tester.pumpAndSettle();
  }

  Future<void> useLocale(WidgetTester tester, Locale locale) async {
    await tester.element(find.byType(SizedBox)).setLocale(locale);
    await tester.pumpAndSettle();
  }

  testWidgets('every enum value renders a real label, never the wire value', (
    tester,
  ) async {
    await pumpScope(tester);

    for (final locale in const [Locale('en'), Locale('de')]) {
      await useLocale(tester, locale);

      for (final status in ProjectStatus.values) {
        if (status == ProjectStatus.unknown) continue;
        final label = EnumLabels.projectStatus(status);
        expect(label, isNotEmpty);
        expect(
          label,
          isNot(contains('enums.')),
          reason: 'missing translation for ${status.name} in $locale',
        );
      }

      for (final status in WorkItemStatus.values) {
        if (status == WorkItemStatus.unknown) continue;
        final label = EnumLabels.workItemStatus(status);
        expect(
          label,
          isNot(contains('enums.')),
          reason: 'missing translation for ${status.name} in $locale',
        );
      }

      for (final status in AgentRunStatus.values) {
        if (status == AgentRunStatus.unknown) continue;
        final label = EnumLabels.agentRunStatus(status);
        expect(
          label,
          isNot(contains('enums.')),
          reason: 'missing translation for ${status.name} in $locale',
        );
      }

      for (final category in FailureCategory.values) {
        if (category == FailureCategory.unknown) continue;
        final label = EnumLabels.failureCategory(category);
        expect(
          label,
          isNot(contains('enums.')),
          reason: 'missing translation for ${category.name} in $locale',
        );
      }
    }
  });

  testWidgets('labels differ between English and German', (tester) async {
    await pumpScope(tester);

    await useLocale(tester, const Locale('en'));
    final english = EnumLabels.projectStatus(ProjectStatus.humanReview);

    await useLocale(tester, const Locale('de'));
    final german = EnumLabels.projectStatus(ProjectStatus.humanReview);

    expect(english, 'Human review');
    expect(german, 'Menschliche Freigabe');
  });

  testWidgets('an unparsed value is shown as unknown, with the raw value', (
    tester,
  ) async {
    await pumpScope(tester);
    await useLocale(tester, const Locale('en'));
    final label = EnumLabels.projectStatus(
      ProjectStatus.unknown,
      rawValue: 'QuantumReview',
    );

    expect(label, contains('QuantumReview'));
    expect(label.toLowerCase(), contains('unknown'));

    await useLocale(tester, const Locale('de'));
    final germanLabel = EnumLabels.projectStatus(
      ProjectStatus.unknown,
      rawValue: 'QuantumReview',
    );

    expect(germanLabel, contains('QuantumReview'));
    expect(germanLabel.toLowerCase(), contains('unbekannt'));
  });

  group('wire values', () {
    test('parse case-insensitively and never throw', () {
      expect(ProjectStatus.fromWire('HumanReview'), ProjectStatus.humanReview);
      expect(ProjectStatus.fromWire('humanreview'), ProjectStatus.humanReview);
      expect(ProjectStatus.fromWire(' Paused '), ProjectStatus.paused);
      expect(ProjectStatus.fromWire(null), ProjectStatus.unknown);
      expect(ProjectStatus.fromWire('Nonsense'), ProjectStatus.unknown);

      expect(WorkItemStatus.fromWire('Blocked'), WorkItemStatus.blocked);
      expect(AgentRunStatus.fromWire('TimedOut'), AgentRunStatus.timedOut);
      expect(FailureCategory.fromWire('Provider'), FailureCategory.provider);
      expect(
        FailureCategory.fromWire('Unknown'),
        FailureCategory.unknownCategory,
      );
    });

    test('match the backend enum names exactly', () {
      expect(ProjectStatus.humanReview.wireValue, 'HumanReview');
      expect(ProjectStatus.completed.wireValue, 'Completed');
      expect(AgentRunStatus.timedOut.wireValue, 'TimedOut');
      expect(WorkItemStatus.repairing.wireValue, 'Repairing');
    });

    test('terminal states match the backend transition tables', () {
      expect(ProjectStatus.completed.isTerminal, isTrue);
      expect(ProjectStatus.failed.isTerminal, isTrue);
      expect(ProjectStatus.cancelled.isTerminal, isTrue);
      expect(ProjectStatus.humanReview.isTerminal, isFalse);
      expect(ProjectStatus.paused.isTerminal, isFalse);

      expect(AgentRunStatus.succeeded.isTerminal, isTrue);
      expect(AgentRunStatus.waiting.isTerminal, isFalse);
    });
  });
}
