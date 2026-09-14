using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Learning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.13 — ported from AutonomousCodingOrchestrator's review block
/// (ExecuteWorkItemCoreAsync) and EvaluateAndLearnAsync, adapted to run
/// against a passed-in AgentRun rather than legacy HermesExecution/
/// ITaskStateManager. Never re-decides build/test outcome — verification
/// already happened in IVerificationGate before this is called.
/// </summary>
public sealed class WorkItemCompletionProcessor : IWorkItemCompletionProcessor
{
    private readonly IValidationRunRepository _validationRunRepository;
    private readonly IReviewContextBuilder _reviewContextBuilder;
    private readonly ICodeReviewer _codeReviewer;
    private readonly IReviewRunRepository _reviewRunRepository;
    private readonly ReviewOptions _reviewOptions;
    private readonly IEvaluationEngine _evaluationEngine;
    private readonly IEvaluationRunRepository _evaluationRunRepository;
    private readonly LearningService _learningService;
    private readonly ILogger<WorkItemCompletionProcessor> _logger;

    public WorkItemCompletionProcessor(
        IValidationRunRepository validationRunRepository,
        IReviewContextBuilder reviewContextBuilder,
        ICodeReviewer codeReviewer,
        IReviewRunRepository reviewRunRepository,
        ReviewOptions reviewOptions,
        IEvaluationEngine evaluationEngine,
        IEvaluationRunRepository evaluationRunRepository,
        LearningService learningService,
        ILogger<WorkItemCompletionProcessor> logger)
    {
        _validationRunRepository = validationRunRepository;
        _reviewContextBuilder = reviewContextBuilder;
        _codeReviewer = codeReviewer;
        _reviewRunRepository = reviewRunRepository;
        _reviewOptions = reviewOptions;
        _evaluationEngine = evaluationEngine;
        _evaluationRunRepository = evaluationRunRepository;
        _learningService = learningService;
        _logger = logger;
    }

    public async Task<WorkItemCompletionDecision> ProcessAsync(
        Project project,
        WorkItem workItem,
        AgentRun agentRun,
        VerificationResult verificationResult,
        CancellationToken cancellationToken = default)
    {
        // VerificationGate itself never persists a ValidationRun row — this
        // exists solely so IReviewContextBuilder.BuildAsync (unmodified,
        // reads the latest ValidationRun via IValidationRunRepository) has
        // real data to read. Verification only reaches this processor on
        // VerificationStatus.Passed, so both flags are always true here.
        await _validationRunRepository.PersistAsync(new ValidationRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            Success = true,
            BuildSucceeded = true,
            BuildOutput = verificationResult.Summary,
            TestsSucceeded = true,
            TestOutput = verificationResult.Summary,
            FailuresJson = "[]",
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        var reviewContext = await _reviewContextBuilder.BuildAsync(workItem, cancellationToken);
        var reviewResult = await _codeReviewer.ReviewAsync(reviewContext, cancellationToken);

        await _reviewRunRepository.PersistAsync(new ReviewRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            BlocksCompletion = reviewResult.BlocksCompletion,
            FindingsJson = JsonSerializer.Serialize(reviewResult.Findings),
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        var mediumBlocks = _reviewOptions.BlockOnMediumFindings
            && reviewResult.Findings.Any(f => f.Severity == ReviewFindingSeverity.Medium);

        if (reviewResult.BlocksCompletion || mediumBlocks)
        {
            var reviewErrorSummary = string.Join(
                "; ",
                reviewResult.Findings
                    .Where(f => f.Severity is ReviewFindingSeverity.Critical or ReviewFindingSeverity.High
                        || (mediumBlocks && f.Severity == ReviewFindingSeverity.Medium))
                    .Select(f => $"[{f.Severity}/{f.Category}] {f.Description}")
                    .Take(5));

            _logger.LogInformation(
                "WorkItemCompletionReviewBlocked WorkItemId={WorkItemId} AgentRunId={AgentRunId}",
                workItem.Id, agentRun.Id);

            return new WorkItemCompletionDecision(
                WorkItemStatus.Repairing,
                string.IsNullOrWhiteSpace(reviewErrorSummary)
                    ? "Independent review blocked completion."
                    : reviewErrorSummary);
        }

        // Review passed — evaluation + learning is a best-effort side
        // channel: a failure here must never change the WorkItem away from
        // Completed (mirrors legacy, which only ran this after the WorkItem
        // was already marked Completed).
        try
        {
            await EvaluateAndLearnAsync(project, workItem, agentRun, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "WorkItemCompletionEvaluationFailed WorkItemId={WorkItemId} AgentRunId={AgentRunId}",
                workItem.Id, agentRun.Id);
        }

        return new WorkItemCompletionDecision(WorkItemStatus.Completed, null);
    }

    private async Task EvaluateAndLearnAsync(
        Project project, WorkItem workItem, AgentRun agentRun, CancellationToken cancellationToken)
    {
        var evaluation = await _evaluationEngine.EvaluateAsync(workItem, cancellationToken);

        await _evaluationRunRepository.PersistAsync(new EvaluationRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            BuildSucceeded = evaluation.BuildSucceeded,
            TestsSucceeded = evaluation.TestsSucceeded,
            ReviewBlocked = evaluation.ReviewBlocked,
            HumanApproval = evaluation.HumanApproval,
            RepairCount = evaluation.RepairCount,
            Regression = evaluation.Regression.ToString(),
            IsFullyValidated = evaluation.IsFullyValidated,
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        _logger.LogInformation(
            "Evaluation recorded for WorkItem {WorkItemId}: IsFullyValidated={IsFullyValidated}, RepairCount={RepairCount}, Regression={Regression}",
            workItem.Id, evaluation.IsFullyValidated, evaluation.RepairCount, evaluation.Regression);

        var lesson = await _learningService.RecordLessonAsync(
            workItem.ProjectId,
            workItem.Type.ToString(),
            problem: workItem.Title,
            solution: agentRun.Output ?? string.Empty,
            evidence:
                $"build={evaluation.BuildSucceeded}; tests={evaluation.TestsSucceeded}; " +
                $"reviewBlocked={evaluation.ReviewBlocked}; repairs={evaluation.RepairCount}; " +
                $"regression={evaluation.Regression}",
            confidence: evaluation.IsFullyValidated ? 0.9 : 0.5,
            source: $"work-item:{workItem.Id}",
            evaluation: evaluation,
            cancellationToken: cancellationToken);

        if (lesson is not null)
        {
            _logger.LogInformation(
                "Lesson recorded for WorkItem {WorkItemId}: Level={Level}", workItem.Id, lesson.Level);
        }
    }
}
