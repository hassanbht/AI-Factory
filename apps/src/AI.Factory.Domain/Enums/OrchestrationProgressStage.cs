namespace AI.Factory.Domain.Enums;

public enum OrchestrationProgressStage
{
    RunCreated,
    ContextRetrieved,
    PlanningStarted,
    PlanReady,
    ModelSelected,
    AgentStarted,
    AgentCompleted,
    BuildStarted,
    BuildCompleted,
    TestsStarted,
    TestsCompleted,
    RepairStarted,
    RepairCompleted,
    ReviewStarted,
    ReviewCompleted,
    EvaluationCompleted,
    LearningCompleted,
    AwaitingApproval,
    Completed,
    Failed
}
