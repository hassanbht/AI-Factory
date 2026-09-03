using System.Diagnostics;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Approvals;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Learning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

public sealed class AutonomousCodingOrchestrator : IAutonomousCodingOrchestrator
{
    // Rough placeholder — no real tokenizer is wired up; a real estimate
    // would need one, which is unnecessary infrastructure for this phase.
    private const int DefaultContextTokenEstimate = 8_000;
    private const int ContextDocumentsPerCategory = 5;

    private readonly ITaskScheduler _scheduler;
    private readonly ITaskStateManager _taskStateManager;
    private readonly IAgentExecutor _agentExecutor;
    private readonly IAgentRunRepository _agentRunRepository;
    private readonly IArtifactRecorder _artifactRecorder;
    private readonly IContextProvider _contextProvider;
    private readonly IModelRouter _modelRouter;
    private readonly IProjectRepository _projectRepository;
    private readonly IValidationEngine _validationEngine;
    private readonly IValidationRunRepository _validationRunRepository;
    private readonly ApprovalService _approvalService;
    private readonly RepairOptions _repairOptions;
    private readonly ICodeReviewer _codeReviewer;
    private readonly IReviewContextBuilder _reviewContextBuilder;
    private readonly IReviewRunRepository _reviewRunRepository;
    private readonly ReviewOptions _reviewOptions;
    private readonly IEvaluationEngine _evaluationEngine;
    private readonly IEvaluationRunRepository _evaluationRunRepository;
    private readonly LearningService _learningService;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly IArtifactRepository _artifactRepository;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly IAuditLogger _auditLogger;
    private readonly IOrchestrationProgressReporter _progressReporter;
    private readonly IOrchestrationRunTraceService _traceService;
    private readonly IOperationScopeFactory _operationScopeFactory;
    private readonly IApplicationShutdown? _shutdown;
    private readonly ILogger<AutonomousCodingOrchestrator> _logger;

    public AutonomousCodingOrchestrator(
        ITaskScheduler scheduler,
        ITaskStateManager taskStateManager,
        IAgentExecutor agentExecutor,
        IAgentRunRepository agentRunRepository,
        IArtifactRecorder artifactRecorder,
        IContextProvider contextProvider,
        IModelRouter modelRouter,
        IProjectRepository projectRepository,
        IValidationEngine validationEngine,
        IValidationRunRepository validationRunRepository,
        ApprovalService approvalService,
        RepairOptions repairOptions,
        ICodeReviewer codeReviewer,
        IReviewContextBuilder reviewContextBuilder,
        IReviewRunRepository reviewRunRepository,
        ReviewOptions reviewOptions,
        IEvaluationEngine evaluationEngine,
        IEvaluationRunRepository evaluationRunRepository,
        LearningService learningService,
        IWorkItemRepository workItemRepository,
        IArtifactRepository artifactRepository,
        ExecutionLimitsOptions executionLimits,
        IAuditLogger? auditLogger,
        IOrchestrationProgressReporter? progressReporter,
        IOrchestrationRunTraceService? traceService,
        IOperationScopeFactory? operationScopeFactory,
        ILogger<AutonomousCodingOrchestrator> logger,
        IApplicationShutdown? shutdown = null)
    {
        _scheduler = scheduler;
        _taskStateManager = taskStateManager;
        _agentExecutor = agentExecutor;
        _agentRunRepository = agentRunRepository;
        _artifactRecorder = artifactRecorder;
        _contextProvider = contextProvider;
        _modelRouter = modelRouter;
        _projectRepository = projectRepository;
        _validationEngine = validationEngine;
        _validationRunRepository = validationRunRepository;
        _approvalService = approvalService;
        _repairOptions = repairOptions;
        _codeReviewer = codeReviewer;
        _reviewContextBuilder = reviewContextBuilder;
        _reviewRunRepository = reviewRunRepository;
        _reviewOptions = reviewOptions;
        _evaluationEngine = evaluationEngine;
        _evaluationRunRepository = evaluationRunRepository;
        _learningService = learningService;
        _workItemRepository = workItemRepository;
        _artifactRepository = artifactRepository;
        _executionLimits = executionLimits;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        _progressReporter = progressReporter ?? NullOrchestrationProgressReporter.Instance;
        _traceService = traceService ?? NullOrchestrationRunTraceService.Instance;
        _operationScopeFactory = operationScopeFactory ?? NullOperationScopeFactory.Instance;
        _shutdown = shutdown;
        _logger = logger;
    }

    public async Task<WorkItemExecutionOutcome?> ExecuteNextReadyTaskAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (_shutdown?.IsStopping == true || cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown or cancellation in progress; skipping ExecuteNextReadyTaskAsync.");
            return null;
        }

        var workItem = await _scheduler.GetNextReadyWorkItemAsync(projectId, cancellationToken);

        if (workItem is null)
        {
            return null;
        }

        return await ExecuteWorkItemAsync(workItem, projectId, cancellationToken);
    }

    /// <summary>
    /// Executes one specific WorkItem regardless of scheduler ordering —
    /// the entry point an API-triggered OrchestrationRun uses (it targets
    /// one explicit task, not "whatever's next"). Callers driving a
    /// repair loop across multiple attempts call this repeatedly; each
    /// call is exactly one attempt, identical to how ExecuteNextReadyTaskAsync
    /// has always behaved for the autonomous scheduler.
    /// </summary>
    public async Task<WorkItemExecutionOutcome?> ExecuteSpecificWorkItemAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default)
    {
        if (_shutdown?.IsStopping == true || cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown or cancellation in progress; rejecting ExecuteSpecificWorkItemAsync for {WorkItemId}.", workItemId);
            throw new OperationCanceledException("Execution was cancelled due to application shutdown.");
        }

        var workItem = await _workItemRepository.GetByIdAsync(workItemId, cancellationToken)
            ?? throw new NotFoundException($"WorkItem '{workItemId}' was not found.");

        return await ExecuteWorkItemAsync(workItem, workItem.ProjectId, cancellationToken);
    }

    /// <summary>
    /// Wraps the real execution in one correlated IOperationScope so every
    /// _auditLogger.LogAsync call inside ExecuteWorkItemCoreAsync picks up
    /// the same CorrelationId (= executionId) automatically via the ambient
    /// IOperationContext — no individual audit call site needs to change.
    /// </summary>
    private async Task<WorkItemExecutionOutcome?> ExecuteWorkItemAsync(
        WorkItem workItem,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var executionId = Guid.NewGuid();

        await using var operationScope = _operationScopeFactory.Begin(
            "ExecuteWorkItem", correlationId: executionId, projectId: workItem.ProjectId, taskId: workItem.Id);

        using var linkedCts = _shutdown is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.StoppingToken)
            : null;
        var effectiveToken = linkedCts?.Token ?? cancellationToken;

        try
        {
            var outcome = await ExecuteWorkItemCoreAsync(workItem, projectId, executionId, effectiveToken);

            if (outcome is { Success: false })
            {
                operationScope.Fail(outcome.Error ?? "WorkItem execution failed.");
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            operationScope.Fail("Execution was cancelled.");

            try
            {
                var freshItem = await _workItemRepository.GetByIdAsync(workItem.Id, CancellationToken.None);
                if (freshItem is not null && freshItem.Status == WorkItemStatus.Running.ToString())
                {
                    freshItem.Status = freshItem.AttemptCount > 1
                        ? WorkItemStatus.Repairing.ToString()
                        : WorkItemStatus.Ready.ToString();
                    freshItem.CurrentError = "Execution interrupted by shutdown/cancellation; queued for resume.";
                    freshItem.UpdatedAtUtc = DateTime.UtcNow;
                    await _workItemRepository.UpdateAsync(freshItem, CancellationToken.None);
                }
            }
            catch (Exception updateEx)
            {
                _logger.LogWarning(updateEx, "Failed to revert WorkItem {WorkItemId} state on cancellation.", workItem.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            operationScope.Fail(ex);
            throw;
        }
    }

    private async Task<WorkItemExecutionOutcome?> ExecuteWorkItemCoreAsync(
        WorkItem workItem,
        Guid projectId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var workflowStopwatch = Stopwatch.StartNew();

        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ExecutionId"] = executionId
        });

        var started = await _taskStateManager.TryStartAsync(workItem.Id, cancellationToken);

        if (!started)
        {
            // Raced with another execution — not our job now.
            return null;
        }

        // TryStartAsync already incremented AttemptCount (EF identity map:
        // this WorkItem instance is tracked by the same DbContext), so a
        // count greater than 1 here means this is a retry/repair attempt.
        var isRetry = workItem.AttemptCount > 1;

        _logger.LogInformation(
            "Task started: WorkItem {WorkItemId} ({Title}), attempt {AttemptCount}, ExecutionId {ExecutionId}",
            workItem.Id, workItem.Title, workItem.AttemptCount, executionId);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.WorkflowStarted,
            AuditSeverity.Info,
            $"Execution started for WorkItem '{workItem.Id}' ({workItem.Title}), attempt {workItem.AttemptCount}.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

        if (isRetry)
        {
            await _auditLogger.LogAsync(
                executionId,
                AuditEventType.RepairAttempt,
                AuditSeverity.Info,
                $"Repair attempt {workItem.AttemptCount} for WorkItem '{workItem.Id}'.",
                projectId: workItem.ProjectId,
                workItemId: workItem.Id,
                cancellationToken: cancellationToken);
        }

        var repairContextText = string.Empty;

        if (isRetry)
        {
            await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.Repair, workItem.AttemptCount, cancellationToken: cancellationToken);

            try
            {
                repairContextText = await BuildRepairContextTextAsync(workItem, cancellationToken);
            }
            catch (Exception ex)
            {
                await _traceService.FailStepAsync(
                    workItem.Id, OrchestrationStepKind.Repair, ex.GetType().Name, ex.Message, workItem.AttemptCount, cancellationToken);
                throw;
            }

            await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.Repair, workItem.AttemptCount, cancellationToken: cancellationToken);
        }

        await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.ContextRetrieval, workItem.AttemptCount, cancellationToken: cancellationToken);

        string contextText;

        try
        {
            contextText = await BuildContextAsync(projectId, cancellationToken);
        }
        catch (Exception ex)
        {
            await _traceService.FailStepAsync(
                workItem.Id, OrchestrationStepKind.ContextRetrieval, ex.GetType().Name, ex.Message, workItem.AttemptCount, cancellationToken);
            throw;
        }

        await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.ContextRetrieval, workItem.AttemptCount, cancellationToken: cancellationToken);

        var agentName = SelectAgent(workItem.Type);

        // IModelRouter.SelectModel is synchronous/pure (no real model call
        // exists yet — see Phase 5) — a wall-clock timeout here would be
        // theater; honoring cancellation is the correct, complete contract.
        cancellationToken.ThrowIfCancellationRequested();

        await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.ModelSelection, workItem.AttemptCount, cancellationToken: cancellationToken);

        var modelSelection = _modelRouter.SelectModel(new ModelRoutingRequest(
            MapCategory(workItem.Type),
            TaskComplexity.Standard,
            DefaultContextTokenEstimate,
            CostPreference.Balanced));

        await _traceService.CompleteStepAsync(
            workItem.Id, OrchestrationStepKind.ModelSelection, workItem.AttemptCount,
            new Dictionary<string, string> { ["model"] = modelSelection.ModelId, ["provider"] = modelSelection.Provider },
            cancellationToken);

        _logger.LogInformation(
            "Model selected for WorkItem {WorkItemId}: {ModelId} ({Reason})",
            workItem.Id, modelSelection.ModelId, modelSelection.Reason);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.ModelSelected,
            AuditSeverity.Info,
            $"Model '{modelSelection.ModelId}' selected: {modelSelection.Reason}",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            metadata: new Dictionary<string, string> { ["agent"] = agentName, ["model"] = modelSelection.ModelId },
            cancellationToken: cancellationToken);

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.ModelSelected,
            $"Model '{modelSelection.ModelId}' selected.", cancellationToken);

        var prompt = BuildPrompt(workItem, contextText, repairContextText);
        var agentRunId = Guid.NewGuid();

        await _agentRunRepository.CreatePendingAsync(new AgentRun
        {
            Id = agentRunId,
            WorkItemId = workItem.Id,
            Agent = agentName,
            Model = modelSelection.ModelId,
            Provider = modelSelection.Provider,
            Prompt = prompt,
            AttemptNumber = workItem.AttemptCount,
            Success = false,
            StartedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.AgentStarted,
            AuditSeverity.Info,
            $"Agent '{agentName}' started for WorkItem '{workItem.Id}'.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.AgentStarted,
            $"Agent '{agentName}' started.", cancellationToken);

        await _traceService.StartStepAsync(
            workItem.Id, OrchestrationStepKind.AgentExecution, workItem.AttemptCount,
            new Dictionary<string, string> { ["agent"] = agentName, ["model"] = modelSelection.ModelId, ["provider"] = modelSelection.Provider },
            cancellationToken);

        AgentExecutionResult result;
        var agentExecutionTimeout = TimeSpan.FromMinutes(_executionLimits.AgentExecutionTimeoutMinutes);

        try
        {
            result = await TimeoutGuard.RunAsync(
                ct => _agentExecutor.ExecuteAsync(
                    new AgentExecutionRequest(agentName, modelSelection.ModelId, prompt, workItem.ProjectId.ToString()),
                    ct),
                agentExecutionTimeout,
                "AgentExecution",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                "Execution timeout: WorkItem {WorkItemId}, Operation={Operation}, ConfiguredLimit={ConfiguredLimit}, ExecutionId {ExecutionId}",
                workItem.Id, "AgentExecution", agentExecutionTimeout, executionId);

            await _auditLogger.LogAsync(
                executionId,
                AuditEventType.Timeout,
                AuditSeverity.Warning,
                ex.Message,
                projectId: workItem.ProjectId,
                workItemId: workItem.Id,
                metadata: new Dictionary<string, string> { ["operation"] = "AgentExecution" },
                cancellationToken: cancellationToken);

            await _agentRunRepository.CompleteAsync(agentRunId, false, null, ex.Message, 0, cancellationToken);

            await _traceService.FailStepAsync(
                workItem.Id, OrchestrationStepKind.AgentExecution, "Timeout", ex.Message, workItem.AttemptCount, cancellationToken);

            return await HandleFailureAsync(
                workItem, PipelineFailureCategory.Infrastructure, ex.Message, executionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution threw for WorkItem {WorkItemId}", workItem.Id);

            await _agentRunRepository.CompleteAsync(agentRunId, false, null, ex.Message, 0, cancellationToken);

            await _traceService.FailStepAsync(
                workItem.Id, OrchestrationStepKind.AgentExecution, ex.GetType().Name, ex.Message, workItem.AttemptCount, cancellationToken);

            return await HandleFailureAsync(
                workItem, PipelineFailureCategory.Infrastructure, ex.Message, executionId, cancellationToken);
        }

        await _agentRunRepository.CompleteAsync(
            agentRunId, result.Success, result.Output, result.Error, result.DurationMs, cancellationToken);

        if (result.Success)
        {
            await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.AgentExecution, workItem.AttemptCount, cancellationToken: cancellationToken);
        }
        else
        {
            await _traceService.FailStepAsync(
                workItem.Id, OrchestrationStepKind.AgentExecution, "AgentFailure",
                result.Error ?? "Agent execution failed.", workItem.AttemptCount, cancellationToken);
        }

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.AgentCompleted,
            result.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            result.Success ? "Agent execution completed successfully." : (result.Error ?? "Agent execution failed."),
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            durationMs: result.DurationMs,
            cancellationToken: cancellationToken);

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.AgentCompleted,
            result.Success ? "Agent execution completed." : (result.Error ?? "Agent execution failed."),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.WorkspacePath))
        {
            await _artifactRecorder.RecordWorkspaceArtifactsAsync(
                workItem.ProjectId, workItem.Id, result.WorkspacePath, cancellationToken);
        }

        if (!result.Success)
        {
            // Agent execution itself failed — nothing to build/test.
            return await HandleFailureAsync(
                workItem,
                PipelineFailureCategory.AgentExecution,
                result.Error ?? "Agent execution failed.",
                executionId,
                cancellationToken);
        }

        await _taskStateManager.HeartbeatAsync(workItem.Id, "AgentExecutionCompleted", cancellationToken);

        if (_executionLimits.ExceedsWorkflowDuration(workflowStopwatch.Elapsed))
        {
            return await HandleWorkflowTimeoutAsync(workItem, executionId, workflowStopwatch.Elapsed, cancellationToken);
        }

        var project = await _projectRepository.GetByIdAsync(workItem.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project '{workItem.ProjectId}' was not found.");

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.BuildStarted, "Build started.", cancellationToken);
        await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.Build, workItem.AttemptCount, cancellationToken: cancellationToken);

        // ValidationEngine.ValidateAsync builds and tests in one call — Build
        // and Test steps are both derived from this single result, not
        // independently timed; see Phase 17.2 report for this simplification.
        var validationResult = await _validationEngine.ValidateAsync(
            new ValidationRequest(workItem.Id, project.RootPath), cancellationToken);

        if (validationResult.BuildSucceeded)
        {
            await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.Build, workItem.AttemptCount, cancellationToken: cancellationToken);
        }
        else
        {
            await _traceService.FailStepAsync(
                workItem.Id, OrchestrationStepKind.Build, "BuildFailure",
                validationResult.BuildOutput ?? "Build failed.", workItem.AttemptCount, cancellationToken);
        }

        await _validationRunRepository.PersistAsync(new ValidationRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            Success = validationResult.Success,
            BuildSucceeded = validationResult.BuildSucceeded,
            BuildOutput = validationResult.BuildOutput,
            TestsSucceeded = validationResult.TestsSucceeded,
            TestOutput = validationResult.TestOutput,
            FailuresJson = JsonSerializer.Serialize(validationResult.Failures),
            CreatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.BuildResult,
            validationResult.BuildSucceeded ? AuditSeverity.Info : AuditSeverity.Warning,
            $"Build {(validationResult.BuildSucceeded ? "succeeded" : "failed")} for WorkItem '{workItem.Id}'.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.BuildCompleted,
            validationResult.BuildSucceeded ? "Build succeeded." : "Build failed.", cancellationToken);

        if (validationResult.TestsSucceeded is not null)
        {
            await _progressReporter.ReportAsync(
                workItem.Id, OrchestrationProgressStage.TestsStarted, "Tests started.", cancellationToken);
            await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.Test, workItem.AttemptCount, cancellationToken: cancellationToken);

            await _auditLogger.LogAsync(
                executionId,
                AuditEventType.TestResult,
                validationResult.TestsSucceeded == true ? AuditSeverity.Info : AuditSeverity.Warning,
                $"Tests {(validationResult.TestsSucceeded == true ? "passed" : "failed")} for WorkItem '{workItem.Id}'.",
                projectId: workItem.ProjectId,
                workItemId: workItem.Id,
                cancellationToken: cancellationToken);

            await _progressReporter.ReportAsync(
                workItem.Id, OrchestrationProgressStage.TestsCompleted,
                validationResult.TestsSucceeded == true ? "Tests passed." : "Tests failed.", cancellationToken);

            if (validationResult.TestsSucceeded == true)
            {
                await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.Test, workItem.AttemptCount, cancellationToken: cancellationToken);
            }
            else
            {
                await _traceService.FailStepAsync(
                    workItem.Id, OrchestrationStepKind.Test, "TestFailure",
                    validationResult.TestOutput ?? "Tests failed.", workItem.AttemptCount, cancellationToken);
            }
        }
        else
        {
            await _traceService.SkipStepAsync(
                workItem.Id, OrchestrationStepKind.Test,
                "No tests ran (build failed before tests could execute, or no test target).",
                workItem.AttemptCount, cancellationToken);
        }

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.ValidationResult,
            validationResult.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            $"Validation {(validationResult.Success ? "passed" : "failed")} for WorkItem '{workItem.Id}'.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

        if (_executionLimits.ExceedsWorkflowDuration(workflowStopwatch.Elapsed))
        {
            return await HandleWorkflowTimeoutAsync(workItem, executionId, workflowStopwatch.Elapsed, cancellationToken);
        }

        if (validationResult.Success)
        {
            await _taskStateManager.HeartbeatAsync(workItem.Id, "ValidationPassed", cancellationToken);

            // Build+test passed at the agent's own workspace level, but the
            // task is still not "done" — an independent review gates final
            // completion. This is a structurally separate agent invocation;
            // the implementation agent can never mark its own work reviewed.
            await _progressReporter.ReportAsync(
                workItem.Id, OrchestrationProgressStage.ReviewStarted, "Independent review started.", cancellationToken);
            await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.CodeReview, workItem.AttemptCount, cancellationToken: cancellationToken);

            var reviewContext = await _reviewContextBuilder.BuildAsync(workItem, cancellationToken);
            var reviewResult = await _codeReviewer.ReviewAsync(reviewContext, cancellationToken);

            // The review itself running to completion is what "succeeded"
            // means for this step — BlocksCompletion is a business decision
            // recorded as metadata, not a step failure.
            await _traceService.CompleteStepAsync(
                workItem.Id, OrchestrationStepKind.CodeReview, workItem.AttemptCount,
                new Dictionary<string, string> { ["blocksCompletion"] = reviewResult.BlocksCompletion.ToString() },
                cancellationToken);

            await _progressReporter.ReportAsync(
                workItem.Id, OrchestrationProgressStage.ReviewCompleted,
                reviewResult.BlocksCompletion ? "Review blocked completion." : "Review passed.", cancellationToken);

            await _auditLogger.LogAsync(
                executionId,
                AuditEventType.ReviewResult,
                reviewResult.BlocksCompletion ? AuditSeverity.Warning : AuditSeverity.Info,
                reviewResult.BlocksCompletion ? "Independent review blocked completion." : "Independent review passed.",
                projectId: workItem.ProjectId,
                workItemId: workItem.Id,
                cancellationToken: cancellationToken);

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

            if (!reviewResult.BlocksCompletion && !mediumBlocks)
            {
                await _taskStateManager.HeartbeatAsync(workItem.Id, "ReviewPassed", cancellationToken);
                await _taskStateManager.CompleteAsync(workItem.Id, cancellationToken);

                _logger.LogInformation("Task completed: WorkItem {WorkItemId}", workItem.Id);

                await EvaluateAndLearnAsync(workItem, executionId, cancellationToken);
                await MaybeRequestFinalApprovalAsync(projectId, workItem.Id, cancellationToken);

                await _auditLogger.LogAsync(
                    executionId,
                    AuditEventType.WorkflowCompleted,
                    AuditSeverity.Info,
                    $"WorkItem '{workItem.Id}' completed successfully.",
                    projectId: workItem.ProjectId,
                    workItemId: workItem.Id,
                    cancellationToken: cancellationToken);

                await _progressReporter.ReportAsync(
                    workItem.Id, OrchestrationProgressStage.Completed, "Completed successfully.", cancellationToken);

                return new WorkItemExecutionOutcome(workItem.Id, true, null);
            }

            var reviewErrorSummary = string.Join(
                "; ",
                reviewResult.Findings
                    .Where(f => f.Severity is ReviewFindingSeverity.Critical or ReviewFindingSeverity.High
                        || (mediumBlocks && f.Severity == ReviewFindingSeverity.Medium))
                    .Select(f => $"[{f.Severity}/{f.Category}] {f.Description}")
                    .Take(5));

            return await HandleFailureAsync(
                workItem,
                PipelineFailureCategory.Review,
                string.IsNullOrWhiteSpace(reviewErrorSummary)
                    ? "Independent review blocked completion."
                    : reviewErrorSummary,
                executionId,
                cancellationToken);
        }

        var repairContext = BuildRepairContext(workItem, validationResult, result);

        _logger.LogWarning(
            "Validation failed for WorkItem {WorkItemId}: {ChangedFileCount} changed file(s), " +
            "{CompilerErrorCount} compiler error(s), {TestFailureCount} test failure(s), " +
            "{PreviousRepairAttempts} previous attempt(s)",
            repairContext.WorkItemId,
            repairContext.ChangedFiles.Count,
            repairContext.CompilerErrors.Count,
            repairContext.TestFailures.Count,
            repairContext.PreviousRepairAttempts);

        var validationErrorSummary = string.Join(
            "; ",
            validationResult.Failures.Select(f => f.Message).Take(5));

        var failureCategory = validationResult.BuildSucceeded
            ? PipelineFailureCategory.Test
            : PipelineFailureCategory.Build;

        return await HandleFailureAsync(
            workItem,
            failureCategory,
            string.IsNullOrWhiteSpace(validationErrorSummary)
                ? "Validation failed."
                : validationErrorSummary,
            executionId,
            cancellationToken);
    }

    private async Task<WorkItemExecutionOutcome> HandleWorkflowTimeoutAsync(
        WorkItem workItem,
        Guid executionId,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var configuredLimit = TimeSpan.FromMinutes(_executionLimits.MaxWorkflowDurationMinutes);

        _logger.LogWarning(
            "Execution timeout: WorkItem {WorkItemId}, Operation={Operation}, ConfiguredLimit={ConfiguredLimit}, Elapsed={Elapsed}, ExecutionId {ExecutionId}",
            workItem.Id, "Workflow", configuredLimit, elapsed, executionId);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.Timeout,
            AuditSeverity.Warning,
            $"Workflow execution exceeded the configured {_executionLimits.MaxWorkflowDurationMinutes}-minute limit.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            metadata: new Dictionary<string, string> { ["operation"] = "Workflow" },
            cancellationToken: cancellationToken);

        return await HandleFailureAsync(
            workItem,
            PipelineFailureCategory.Infrastructure,
            $"Workflow execution exceeded the configured {_executionLimits.MaxWorkflowDurationMinutes}-minute limit.",
            executionId,
            cancellationToken);
    }

    private async Task<WorkItemExecutionOutcome> HandleFailureAsync(
        WorkItem workItem,
        PipelineFailureCategory category,
        string error,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var exhausted = await _taskStateManager.FailAsync(workItem.Id, error, cancellationToken);

        _logger.LogWarning(
            "Task failed: WorkItem {WorkItemId}, Category={FailureCategory}, Exhausted={Exhausted}, Error={Error}",
            workItem.Id, category, exhausted, error);

        await _auditLogger.LogAsync(
            executionId,
            exhausted ? AuditEventType.WorkflowFailed : AuditEventType.Retry,
            exhausted ? AuditSeverity.Error : AuditSeverity.Warning,
            error,
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            metadata: new Dictionary<string, string> { ["category"] = category.ToString() },
            cancellationToken: cancellationToken);

        if (exhausted)
        {
            _logger.LogWarning(
                "Task failed: WorkItem {WorkItemId}, Category={FailureCategory}",
                workItem.Id, PipelineFailureCategory.Repair);

            await _progressReporter.ReportAsync(workItem.Id, OrchestrationProgressStage.Failed, error, cancellationToken);
        }

        if (exhausted && _repairOptions.Policy == RepairExhaustionPolicy.HumanReview)
        {
            await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.HumanApproval, workItem.AttemptCount, cancellationToken: cancellationToken);

            try
            {
                await _approvalService.RequestApprovalAsync(
                    workItem.ProjectId,
                    $"Repair attempts exhausted for WorkItem '{workItem.Id}'.",
                    "system",
                    cancellationToken);

                await _traceService.CompleteStepAsync(workItem.Id, OrchestrationStepKind.HumanApproval, workItem.AttemptCount, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not escalate project {ProjectId} to HumanReview after WorkItem {WorkItemId} exhausted its attempts",
                    workItem.ProjectId, workItem.Id);

                await _traceService.FailStepAsync(
                    workItem.Id, OrchestrationStepKind.HumanApproval, ex.GetType().Name, ex.Message, workItem.AttemptCount, cancellationToken);
            }
        }

        return new WorkItemExecutionOutcome(workItem.Id, false, error, category);
    }

    private static RepairContext BuildRepairContext(
        WorkItem workItem,
        ValidationResult validationResult,
        AgentExecutionResult agentResult)
    {
        var compilerErrors = validationResult.Failures
            .Where(f => f.Category == "Build")
            .Select(f => f.Message)
            .ToList();

        var testFailures = validationResult.Failures
            .Where(f => f.Category == "Test")
            .Select(f => f.Message)
            .ToList();

        var changedFiles = string.IsNullOrWhiteSpace(agentResult.WorkspacePath)
            ? Array.Empty<string>()
            : SafeListFiles(agentResult.WorkspacePath);

        var relevantSource = changedFiles.Count > 0
            ? SafeReadFirst(changedFiles[0])
            : null;

        return new RepairContext(
            workItem.Id,
            changedFiles,
            compilerErrors,
            testFailures,
            relevantSource,
            agentResult.Output ?? agentResult.Error,
            workItem.AttemptCount);
    }

    private static IReadOnlyList<string> SafeListFiles(string workspacePath)
    {
        try
        {
            return Directory.Exists(workspacePath)
                ? Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories).ToList()
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? SafeReadFirst(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<string> BuildContextAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Local project context, historical outputs, previous failures, and
        // learned lessons — all via the same IContextProvider abstraction,
        // whichever implementation is active (Local or OpenViking).
        var categories = new[]
        {
            ContextCategory.ProjectRequirements,
            ContextCategory.ArchitectureDocuments,
            ContextCategory.PreviousTaskOutputs,
            ContextCategory.TestFailures,
            ContextCategory.LearnedLessons
        };

        var sections = new List<string>();

        foreach (var category in categories)
        {
            var result = await _contextProvider.QueryAsync(
                new ContextQuery(projectId, category, ContextDocumentsPerCategory), cancellationToken);

            foreach (var document in result.Documents)
            {
                sections.Add($"[{category}] {document.Source}\n{document.Content}");
            }
        }

        return string.Join("\n\n", sections);
    }

    private async Task<string> BuildRepairContextTextAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        var latestAgentRun = await _agentRunRepository.GetLatestAsync(workItem.Id, cancellationToken);
        var latestValidation = await _validationRunRepository.GetLatestAsync(workItem.Id, cancellationToken);
        var latestReview = await _reviewRunRepository.GetLatestAsync(workItem.Id, cancellationToken);
        var artifacts = await _artifactRepository.GetByWorkItemIdAsync(workItem.Id, cancellationToken);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== REPAIR CONTEXT (previous attempt failed) ===");
        sb.AppendLine($"Previous repair attempts: {Math.Max(0, workItem.AttemptCount - 1)}");

        if (!string.IsNullOrWhiteSpace(workItem.CurrentError))
        {
            sb.AppendLine($"Last recorded error: {workItem.CurrentError}");
        }

        if (latestAgentRun is not null)
        {
            sb.AppendLine($"Previous implementation summary: {latestAgentRun.Response ?? latestAgentRun.Error}");
        }

        if (latestValidation is not null)
        {
            sb.AppendLine($"Build succeeded: {latestValidation.BuildSucceeded}");

            if (latestValidation.TestsSucceeded is not null)
            {
                sb.AppendLine($"Tests succeeded: {latestValidation.TestsSucceeded}");
            }

            if (!latestValidation.BuildSucceeded && !string.IsNullOrWhiteSpace(latestValidation.BuildOutput))
            {
                sb.AppendLine($"Build errors: {latestValidation.BuildOutput}");
            }

            if (latestValidation.TestsSucceeded == false && !string.IsNullOrWhiteSpace(latestValidation.TestOutput))
            {
                sb.AppendLine($"Test failures: {latestValidation.TestOutput}");
            }
        }

        if (latestReview is not null && latestReview.BlocksCompletion)
        {
            sb.AppendLine($"Review findings: {latestReview.FindingsJson}");
        }

        if (artifacts.Count > 0)
        {
            sb.AppendLine($"Previously changed files: {string.Join(", ", artifacts.Select(a => a.Path))}");
        }

        sb.AppendLine();
        sb.AppendLine("Do not repeat the previous failed implementation without addressing the reported failure.");

        return sb.ToString();
    }

    private static string BuildPrompt(WorkItem workItem, string contextText, string repairContextText)
    {
        var acceptance = string.IsNullOrWhiteSpace(workItem.AcceptanceCriteria)
            ? string.Empty
            : $"\n\nAcceptance Criteria:\n{workItem.AcceptanceCriteria}";

        var repairSection = string.IsNullOrWhiteSpace(repairContextText)
            ? string.Empty
            : $"\n\n{repairContextText}";

        return $"{workItem.Title}\n\n{workItem.Description}{acceptance}\n\nContext:\n{contextText}{repairSection}";
    }

    private async Task EvaluateAndLearnAsync(WorkItem workItem, Guid executionId, CancellationToken cancellationToken)
    {
        await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.Evaluation, workItem.AttemptCount, cancellationToken: cancellationToken);

        var evaluation = await _evaluationEngine.EvaluateAsync(workItem, cancellationToken);

        await _traceService.CompleteStepAsync(
            workItem.Id, OrchestrationStepKind.Evaluation, workItem.AttemptCount,
            new Dictionary<string, string> { ["isFullyValidated"] = evaluation.IsFullyValidated.ToString() },
            cancellationToken);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.EvaluationResult,
            evaluation.IsFullyValidated ? AuditSeverity.Info : AuditSeverity.Warning,
            $"Evaluation completed: IsFullyValidated={evaluation.IsFullyValidated}.",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

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

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.EvaluationCompleted,
            $"Evaluation completed: IsFullyValidated={evaluation.IsFullyValidated}.", cancellationToken);

        var latestAgentRun = await _agentRunRepository.GetLatestAsync(workItem.Id, cancellationToken);

        await _traceService.StartStepAsync(workItem.Id, OrchestrationStepKind.Learning, workItem.AttemptCount, cancellationToken: cancellationToken);

        var lesson = await _learningService.RecordLessonAsync(
            workItem.ProjectId,
            workItem.Type.ToString(),
            problem: workItem.Title,
            solution: latestAgentRun?.Response ?? string.Empty,
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

        await _traceService.CompleteStepAsync(
            workItem.Id, OrchestrationStepKind.Learning, workItem.AttemptCount,
            lesson is not null ? new Dictionary<string, string> { ["level"] = lesson.Level.ToString() } : null,
            cancellationToken);

        await _auditLogger.LogAsync(
            executionId,
            AuditEventType.LearningResult,
            AuditSeverity.Info,
            lesson is not null ? $"Lesson recorded ({lesson.Level})." : "No lesson recorded (no evidence).",
            projectId: workItem.ProjectId,
            workItemId: workItem.Id,
            cancellationToken: cancellationToken);

        await _progressReporter.ReportAsync(
            workItem.Id, OrchestrationProgressStage.LearningCompleted,
            lesson is not null ? $"Lesson recorded ({lesson.Level})." : "No lesson recorded (no evidence).",
            cancellationToken);
    }

    private async Task MaybeRequestFinalApprovalAsync(Guid projectId, Guid triggeringWorkItemId, CancellationToken cancellationToken)
    {
        var project = await _projectRepository.GetByIdAsync(projectId, cancellationToken);

        if (project is null)
        {
            return;
        }

        if (project.Status is ProjectStatus.HumanReview or ProjectStatus.Completed
            or ProjectStatus.Failed or ProjectStatus.Cancelled)
        {
            // Already awaiting approval or already terminal — never
            // double-request, never auto-mark Approved.
            return;
        }

        var items = await _workItemRepository.GetByProjectIdAsync(projectId, cancellationToken);

        if (items.Count == 0)
        {
            return;
        }

        var allTerminal = items.All(x =>
            x.Status == WorkItemStatus.Completed.ToString()
            || x.Status == WorkItemStatus.Failed.ToString()
            || x.Status == WorkItemStatus.Cancelled.ToString());

        var anyCompleted = items.Any(x => x.Status == WorkItemStatus.Completed.ToString());
        var anyFailed = items.Any(x => x.Status == WorkItemStatus.Failed.ToString());

        if (!allTerminal || !anyCompleted || anyFailed)
        {
            // Pipeline is not yet cleanly finished — nothing further to do.
            return;
        }

        await _traceService.StartStepAsync(triggeringWorkItemId, OrchestrationStepKind.HumanApproval, cancellationToken: cancellationToken);

        try
        {
            await _approvalService.RequestApprovalAsync(
                projectId,
                "Pipeline completed successfully — awaiting human approval.",
                "system",
                cancellationToken);

            _logger.LogInformation(
                "Human approval requested for project {ProjectId} after successful pipeline completion.",
                projectId);

            await _traceService.CompleteStepAsync(triggeringWorkItemId, OrchestrationStepKind.HumanApproval, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not request final approval for project {ProjectId}.", projectId);

            await _traceService.FailStepAsync(
                triggeringWorkItemId, OrchestrationStepKind.HumanApproval, ex.GetType().Name, ex.Message, cancellationToken: cancellationToken);
        }
    }

    private static string SelectAgent(WorkItemType type)
    {
        return type switch
        {
            WorkItemType.Analysis => "analyst",
            WorkItemType.Specification => "analyst",
            WorkItemType.Architecture => "architect",
            WorkItemType.Planning => "planner",
            WorkItemType.Implementation => "coder",
            WorkItemType.Test => "tester",
            WorkItemType.Review => "reviewer",
            WorkItemType.Repair => "repair-agent",
            WorkItemType.Documentation => "writer",
            _ => "coder"
        };
    }

    private static string MapCategory(WorkItemType type)
    {
        return type switch
        {
            WorkItemType.Analysis => "requirements-analysis",
            WorkItemType.Specification => "requirements-analysis",
            WorkItemType.Architecture => "architecture",
            WorkItemType.Planning => "planning",
            WorkItemType.Implementation => "coding",
            WorkItemType.Test => "testing",
            WorkItemType.Review => "code-review",
            WorkItemType.Repair => "debugging",
            WorkItemType.Documentation => "documentation",
            _ => "coding"
        };
    }
}
