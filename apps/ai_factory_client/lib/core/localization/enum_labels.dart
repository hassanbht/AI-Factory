import 'package:easy_localization/easy_localization.dart';

import '../../domain/enums/backend_enums.dart';

/// Backend enum -> localized display label.
///
/// The backend value is an identifier and stays exactly as the API produced it
/// (`HumanReview`, `TimedOut`). Only the *label* shown to a human is localized,
/// and a translated label is never sent back to the API.
///
/// `unknown` members render as "Unknown (RawValue)" so a state introduced by a
/// newer backend is visible rather than blank. The raw value is supplied by the
/// caller, because the enum itself no longer carries it once parsing failed.
class EnumLabels {
  const EnumLabels._();

  static const String _unknownKey = 'enums.unknown';

  static String projectStatus(ProjectStatus status, {String? rawValue}) {
    if (status == ProjectStatus.unknown) return _unknown(rawValue);
    return 'enums.projectStatus.${status.name}'.tr();
  }

  static String workItemStatus(WorkItemStatus status, {String? rawValue}) {
    if (status == WorkItemStatus.unknown) return _unknown(rawValue);
    return 'enums.workItemStatus.${status.name}'.tr();
  }

  static String agentRunStatus(AgentRunStatus status, {String? rawValue}) {
    if (status == AgentRunStatus.unknown) return _unknown(rawValue);
    return 'enums.agentRunStatus.${status.name}'.tr();
  }

  static String workItemType(WorkItemType type, {String? rawValue}) {
    if (type == WorkItemType.unknown) return _unknown(rawValue);
    return 'enums.workItemType.${type.name}'.tr();
  }

  static String workItemPriority(
    WorkItemPriority priority, {
    String? rawValue,
  }) {
    if (priority == WorkItemPriority.unknown) return _unknown(rawValue);
    return 'enums.workItemPriority.${priority.name}'.tr();
  }

  static String taskReadiness(TaskReadiness readiness, {String? rawValue}) {
    if (readiness == TaskReadiness.unknown) return _unknown(rawValue);
    return 'enums.taskReadiness.${readiness.name}'.tr();
  }

  static String structuredResultStatus(
    StructuredResultStatus status, {
    String? rawValue,
  }) {
    if (status == StructuredResultStatus.unknown) return _unknown(rawValue);
    return 'enums.structuredResultStatus.${status.name}'.tr();
  }

  static String failureCategory(FailureCategory category, {String? rawValue}) {
    if (category == FailureCategory.unknown) return _unknown(rawValue);
    // The backend has its own literal "Unknown" category, which is a real
    // answer and therefore a real label — distinct from a value this client
    // could not parse.
    final key = category == FailureCategory.unknownCategory
        ? 'unknown'
        : category.name;
    return 'enums.failureCategory.$key'.tr();
  }

  static String _unknown(String? rawValue) =>
      _unknownKey.tr(args: [rawValue ?? '?']);

  /// Every translation key this mapper can produce, used by the localization
  /// tests to prove no enum value falls back to a raw wire value in the UI.
  /// A log level, localized. The entry's `outcome` is a business result and
  /// is never routed through here — the backend keeps them apart.
  static String auditSeverity(AuditSeverity severity, {String? rawValue}) {
    if (severity == AuditSeverity.unknown) return _unknown(rawValue);
    return 'enums.auditSeverity.${severity.name}'.tr();
  }

  static List<String> allKeys() => <String>[
    _unknownKey,
    for (final v in ProjectStatus.values)
      if (v != ProjectStatus.unknown) 'enums.projectStatus.${v.name}',
    for (final v in WorkItemStatus.values)
      if (v != WorkItemStatus.unknown) 'enums.workItemStatus.${v.name}',
    for (final v in AgentRunStatus.values)
      if (v != AgentRunStatus.unknown) 'enums.agentRunStatus.${v.name}',
    for (final v in FailureCategory.values)
      if (v != FailureCategory.unknown)
        'enums.failureCategory.${v == FailureCategory.unknownCategory ? 'unknown' : v.name}',
    for (final v in WorkItemType.values)
      if (v != WorkItemType.unknown) 'enums.workItemType.${v.name}',
    for (final v in WorkItemPriority.values)
      if (v != WorkItemPriority.unknown) 'enums.workItemPriority.${v.name}',
    for (final v in TaskReadiness.values)
      if (v != TaskReadiness.unknown) 'enums.taskReadiness.${v.name}',
    for (final v in StructuredResultStatus.values)
      if (v != StructuredResultStatus.unknown)
        'enums.structuredResultStatus.${v.name}',
    for (final v in AuditSeverity.values)
      if (v != AuditSeverity.unknown) 'enums.auditSeverity.${v.name}',
  ];
}
