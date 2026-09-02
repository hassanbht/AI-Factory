namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Distinguishes expected pipeline failure classes for observability
/// (logging) — never collapsed into one generic exception. Each category
/// is already backed by a distinct structured signal elsewhere
/// (AgentExecutionResult, ValidationResult, ReviewResult, ApprovalRequest);
/// this enum exists to tag which one applied at a given failure point.
/// </summary>
public enum PipelineFailureCategory
{
    AgentExecution,
    Build,
    Test,
    Validation,
    Repair,
    Review,
    Infrastructure,
    HumanRejection
}
