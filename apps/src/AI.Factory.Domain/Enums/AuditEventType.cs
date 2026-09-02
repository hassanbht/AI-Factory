namespace AI.Factory.Domain.Enums;

public enum AuditEventType
{
    WorkflowStarted,
    WorkflowStateChanged,
    ModelSelected,
    AgentStarted,
    AgentCompleted,
    Retry,
    Timeout,
    BuildResult,
    TestResult,
    RepairAttempt,
    ValidationResult,
    HumanApproval,
    WorkflowCompleted,
    WorkflowFailed,
    ReviewResult,
    EvaluationResult,
    LearningResult
}
