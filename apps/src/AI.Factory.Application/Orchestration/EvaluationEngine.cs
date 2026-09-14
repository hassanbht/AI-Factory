using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

public sealed class EvaluationEngine : IEvaluationEngine
{
    private readonly IValidationRunRepository _validationRunRepository;
    private readonly IReviewRunRepository _reviewRunRepository;
    private readonly IApprovalRepository _approvalRepository;

    public EvaluationEngine(
        IValidationRunRepository validationRunRepository,
        IReviewRunRepository reviewRunRepository,
        IApprovalRepository approvalRepository)
    {
        _validationRunRepository = validationRunRepository;
        _reviewRunRepository = reviewRunRepository;
        _approvalRepository = approvalRepository;
    }

    public async Task<EvaluationResult> EvaluateAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        var validationHistory = await _validationRunRepository.GetHistoryAsync(workItem.Id, cancellationToken);
        var latestValidation = validationHistory.Count > 0 ? validationHistory[^1] : null;

        var latestReview = await _reviewRunRepository.GetLatestAsync(workItem.Id, cancellationToken);
        var latestApproval = await _approvalRepository.GetLatestForProjectAsync(workItem.ProjectId, cancellationToken);

        var regression = ComputeRegression(validationHistory);

        // Phase 18.13 — WorkItem.AttemptCount is only ever incremented by
        // the legacy ITaskStateManager.TryStartAsync path (dead once this
        // phase lands). The AgentRun/TaskDispatchScheduler pipeline tracks
        // repairs via WorkItem.RepairAttemptCount instead (incremented by
        // TaskDispatchScheduler.DispatchRepairAsync), which is the correct
        // source for AgentRun-origin WorkItems.
        var repairCount = workItem.RepairAttemptCount;

        var buildSucceeded = latestValidation?.BuildSucceeded ?? false;
        var testsSucceeded = latestValidation?.TestsSucceeded;
        var reviewBlocked = latestReview?.BlocksCompletion ?? false;
        var humanApproval = latestApproval?.Decision;

        var isFullyValidated =
            buildSucceeded
            && testsSucceeded == true
            && !reviewBlocked
            && regression != RegressionStatus.Regressed
            && (humanApproval is null || humanApproval == ApprovalDecision.Approved);

        return new EvaluationResult(
            workItem.Id,
            buildSucceeded,
            testsSucceeded,
            reviewBlocked,
            humanApproval,
            repairCount,
            regression,
            isFullyValidated);
    }

    private static RegressionStatus ComputeRegression(IReadOnlyList<ValidationRun> history)
    {
        if (history.Count < 2)
        {
            return RegressionStatus.Unknown;
        }

        var previous = history[^2];
        var latest = history[^1];

        if (previous.TestsSucceeded == true && latest.TestsSucceeded == false)
        {
            return RegressionStatus.Regressed;
        }

        if (previous.TestsSucceeded == false && latest.TestsSucceeded == true)
        {
            return RegressionStatus.Improved;
        }

        return RegressionStatus.None;
    }
}
