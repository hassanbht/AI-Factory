namespace AI.Factory.Domain.Enums;

public enum OrchestrationFailureCategory
{
    PlanningFailure,
    AgentFailure,
    BuildFailure,
    TestFailure,
    ValidationFailure,
    ReviewFailure,
    InfrastructureFailure,
    Timeout,
    Cancelled
}
