using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

public interface IEvaluationEngine
{
    /// <summary>
    /// Aggregates build result, test result, review result, human approval
    /// (if applicable), repair count, and regression status from the
    /// WorkItem's existing audit trail — creates no new facts, purely a
    /// read-side evaluation.
    /// </summary>
    Task<EvaluationResult> EvaluateAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default);
}

public enum RegressionStatus
{
    /// <summary>Fewer than two validation runs exist — nothing to compare.</summary>
    Unknown,
    None,
    Regressed,
    Improved
}

public sealed record EvaluationResult(
    Guid WorkItemId,
    bool BuildSucceeded,
    bool? TestsSucceeded,
    bool ReviewBlocked,
    ApprovalDecision? HumanApproval,
    int RepairCount,
    RegressionStatus Regression,
    /// <summary>True only when build+test+review+approval(if any) are all clean and there is no regression — the gate for a Verified lesson.</summary>
    bool IsFullyValidated);
