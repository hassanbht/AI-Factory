namespace AI.Factory.Domain.Enums;

/// <summary>
/// A coarse phase of orchestration work — orthogonal to OrchestrationStepStatus
/// (Pending/Running/Succeeded/...). Deliberately distinct from
/// OrchestrationProgressStage, which bakes a lifecycle point (Started vs
/// Completed) into the enum value itself (e.g. BuildStarted/BuildCompleted)
/// and is used for the free-text progress log, not a paired step trace.
/// </summary>
public enum OrchestrationStepKind
{
    WorkspacePreparation,
    TaskPlanning,
    ContextRetrieval,
    ModelSelection,
    AgentExecution,
    Build,
    Test,
    Repair,
    CodeReview,
    HumanApproval,
    Evaluation,
    Learning
}
