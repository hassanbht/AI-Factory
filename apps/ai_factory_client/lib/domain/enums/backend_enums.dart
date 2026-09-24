/// Mirrors of the backend enums the UI must display.
///
/// Two rules hold everywhere:
///
/// 1. The *wire value* is the backend identifier and is never translated. It is
///    what the client sends back to the API and what `wireValue` returns.
/// 2. Every enum carries an [unknown] member, so a value introduced by a newer
///    backend degrades to a visible "Unknown (RawValue)" label instead of
///    crashing the client.
///
/// Source of truth: `apps/src/AI.Factory.Domain/Enums/*.cs` — see
/// `docs/flutter-api-contract.md`.
library;

/// Shared parse helper: case-insensitive, never throws.
T _parse<T>(String? raw, List<T> values, String Function(T) wire, T unknown) {
  if (raw == null || raw.trim().isEmpty) return unknown;
  final needle = raw.trim().toLowerCase();
  for (final value in values) {
    if (wire(value).toLowerCase() == needle) return value;
  }
  return unknown;
}

/// `AI.Factory.Domain.Enums.ProjectStatus`
enum ProjectStatus {
  created('Created'),
  analyzing('Analyzing'),
  specification('Specification'),
  architecture('Architecture'),
  planning('Planning'),
  implementation('Implementation'),
  paused('Paused'),
  testing('Testing'),
  repairing('Repairing'),
  reviewing('Reviewing'),
  humanReview('HumanReview'),
  completed('Completed'),
  failed('Failed'),
  cancelled('Cancelled'),
  unknown('');

  const ProjectStatus(this.wireValue);

  final String wireValue;

  static ProjectStatus fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  bool get isTerminal =>
      this == completed || this == failed || this == cancelled;

  /// The Factory is actively advancing the project.
  bool get isActive => switch (this) {
    analyzing ||
    specification ||
    architecture ||
    planning ||
    implementation ||
    testing ||
    repairing ||
    reviewing => true,
    _ => false,
  };
}

/// `AI.Factory.Domain.Enums.WorkItemStatus` — persisted as a free-form string
/// on the backend, so defensive parsing matters most here.
enum WorkItemStatus {
  draft('Draft'),
  ready('Ready'),
  running('Running'),
  testing('Testing'),
  repairing('Repairing'),
  reviewing('Reviewing'),
  completed('Completed'),
  blocked('Blocked'),
  cancelled('Cancelled'),
  failed('Failed'),
  unknown('');

  const WorkItemStatus(this.wireValue);

  final String wireValue;

  static WorkItemStatus fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  bool get isTerminal =>
      this == completed || this == failed || this == cancelled;
}

/// `AI.Factory.Domain.Enums.AgentRunStatus`
enum AgentRunStatus {
  created('Created'),
  queued('Queued'),
  running('Running'),
  waiting('Waiting'),
  succeeded('Succeeded'),
  failed('Failed'),
  timedOut('TimedOut'),
  cancelled('Cancelled'),
  unknown('');

  const AgentRunStatus(this.wireValue);

  final String wireValue;

  static AgentRunStatus fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  bool get isTerminal => switch (this) {
    succeeded || failed || timedOut || cancelled => true,
    _ => false,
  };
}

/// `AI.Factory.Domain.Enums.FailureCategory`
enum FailureCategory {
  unknownCategory('Unknown'),
  validation('Validation'),
  build('Build'),
  test('Test'),
  agent('Agent'),
  model('Model'),
  provider('Provider'),
  timeout('Timeout'),
  cancelled('Cancelled'),
  infrastructure('Infrastructure'),
  approval('Approval'),
  review('Review'),
  unknown('');

  const FailureCategory(this.wireValue);

  final String wireValue;

  static FailureCategory fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  /// The project diagnostics endpoint serializes this enum as its **ordinal**
  /// (no `JsonStringEnumConverter` is configured for raw record responses),
  /// so declaration order here has to match the backend enum exactly.
  /// Out-of-range values degrade to [unknown] rather than throwing.
  static FailureCategory fromOrdinal(int ordinal) =>
      (ordinal >= 0 && ordinal < values.length - 1) ? values[ordinal] : unknown;
}

/// `AI.Factory.Domain.Enums.WorkItemType` — also the `agentType` of a
/// dispatchable AgentDefinition.
enum WorkItemType {
  analysis('Analysis'),
  specification('Specification'),
  architecture('Architecture'),
  planning('Planning'),
  implementation('Implementation'),
  test('Test'),
  review('Review'),
  repair('Repair'),
  documentation('Documentation'),
  unknown('');

  const WorkItemType(this.wireValue);

  final String wireValue;

  static WorkItemType fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);
}

/// `AI.Factory.Domain.Enums.TaskReadinessStatus` — why a work item can or
/// cannot be dispatched right now.
enum TaskReadiness {
  ready('Ready'),
  waitingForDependency('WaitingForDependency'),
  blockedByFailedDependency('BlockedByFailedDependency'),
  running('Running'),
  completed('Completed'),
  cancelled('Cancelled'),
  failed('Failed'),
  unknown('');

  const TaskReadiness(this.wireValue);

  final String wireValue;

  static TaskReadiness fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);
}

/// The Factory-enforced priority values for a work item.
enum WorkItemPriority {
  critical('Critical'),
  high('High'),
  normal('Normal'),
  low('Low'),
  unknown('');

  const WorkItemPriority(this.wireValue);

  final String wireValue;

  static WorkItemPriority fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  /// Highest first, for ordering a work item list.
  int get rank => switch (this) {
    critical => 0,
    high => 1,
    normal => 2,
    low => 3,
    unknown => 4,
  };
}

/// `AI.Factory.Domain.Enums.ApprovalDecision`.
///
/// Reaches the client only inside the project diagnostics summary, where it
/// is serialized as its **ordinal**, not its name — see [fromOrdinal].
enum ApprovalDecision {
  pendingApproval('PendingApproval'),
  approved('Approved'),
  rejected('Rejected'),
  unknown('');

  const ApprovalDecision(this.wireValue);

  final String wireValue;

  static ApprovalDecision fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  /// Declaration order matches the backend enum, so the ordinal is the
  /// contract. Out-of-range values degrade to [unknown].
  static ApprovalDecision fromOrdinal(int ordinal) =>
      (ordinal >= 0 && ordinal < values.length - 1) ? values[ordinal] : unknown;
}

/// `AI.Factory.Domain.Enums.ProjectSourceType` — request-only.
enum ProjectSourceType {
  empty('Empty'),
  gitRepository('GitRepository');

  const ProjectSourceType(this.wireValue);

  final String wireValue;
}

/// `StructuredResultStatus` on a structured agent result.
enum StructuredResultStatus {
  succeeded('Succeeded'),
  failed('Failed'),
  cancelled('Cancelled'),
  timedOut('TimedOut'),

  /// The agent answered, but not in the agreed contract.
  invalid('Invalid'),
  unknown('');

  const StructuredResultStatus(this.wireValue);

  final String wireValue;

  static StructuredResultStatus fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);
}

/// `AI.Factory.Domain.Enums.AuditSeverity` on an audit log entry.
///
/// `GET /api/audit-log` returns the raw entity and the API registers no
/// `JsonStringEnumConverter`, so this arrives as an **ordinal**, exactly like
/// [FailureCategory] on the diagnostics endpoint. Declaration order therefore
/// has to match the backend enum.
///
/// This is a log level, not a business outcome — the entry's own `outcome`
/// field carries that, and the two are never merged.
enum AuditSeverity {
  info('Info'),
  warning('Warning'),
  error('Error'),
  critical('Critical'),
  unknown('');

  const AuditSeverity(this.wireValue);

  final String wireValue;

  static AuditSeverity fromWire(String? raw) =>
      _parse(raw, values, (v) => v.wireValue, unknown);

  /// Out-of-range values degrade to [unknown] rather than throwing, so a
  /// severity a newer backend adds cannot break the audit list.
  static AuditSeverity fromOrdinal(int ordinal) =>
      (ordinal >= 0 && ordinal < values.length - 1) ? values[ordinal] : unknown;

  bool get isFailure => this == warning || this == error || this == critical;
}

/// `AI.Factory.Domain.Enums.AuditEventType`, in backend declaration order.
///
/// Also an ordinal on the wire. There are 47 of them and they are technical
/// identifiers rather than operator-facing prose, so they are **not**
/// translated (Workstream W: identifiers keep their wire spelling in both
/// languages). A name table rather than a Dart enum, because nothing in the
/// client branches on the value — it is displayed and nothing more.
const List<String> auditEventTypeNames = <String>[
  'WorkflowStarted',
  'WorkflowStateChanged',
  'ModelSelected',
  'AgentStarted',
  'AgentCompleted',
  'Retry',
  'Timeout',
  'BuildResult',
  'TestResult',
  'RepairAttempt',
  'ValidationResult',
  'HumanApproval',
  'WorkflowCompleted',
  'WorkflowFailed',
  'ReviewResult',
  'EvaluationResult',
  'LearningResult',
  'CircuitStateChanged',
  'CircuitRejected',
  'BulkheadEntered',
  'BulkheadQueued',
  'BulkheadRejected',
  'BulkheadCompleted',
  'ShutdownInitiated',
  'WorkerStopping',
  'WorkerStopped',
  'OperationsDrained',
  'OperationsCancelled',
  'ShutdownTimedOut',
  'ShutdownRequestRejected',
  'OperationalAccessDenied',
  'RateLimitRejected',
  'SecurityRequestRejected',
  'ConfigurationValidationFailed',
  'SecretResolutionFailed',
  'RequiredSecretMissing',
  'DatabaseRetry',
  'DatabaseConcurrencyConflict',
  'DatabaseTransactionFailed',
  'DatabaseOperationFailed',
  'WorkerDegraded',
  'WorkerRecovered',
  'WorkerFaulted',
  'DatabaseBackupSucceeded',
  'DatabaseBackupFailed',
  'DatabaseRestoreSucceeded',
  'DatabaseRestoreFailed',
];

/// The event type's backend name, or the ordinal itself when the backend has
/// introduced one this client does not know — never a blank and never a guess.
String auditEventTypeName(int ordinal) =>
    (ordinal >= 0 && ordinal < auditEventTypeNames.length)
    ? auditEventTypeNames[ordinal]
    : '#$ordinal';
