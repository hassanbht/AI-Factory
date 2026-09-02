namespace AI.Factory.Domain.Enums;

public enum OrchestrationRunStatus
{
    Pending,
    Claimed,
    Running,
    Planning,
    Executing,
    Building,
    Testing,
    Repairing,
    Reviewing,
    Evaluating,
    AwaitingApproval,
    Completed,
    Failed,
    Cancelled
}
