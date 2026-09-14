using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.13 — rewritten to delegate into the authoritative AgentRun
/// pipeline instead of independently executing the WorkItem. This engine's
/// job is now "prepare and hand off to the one true executor": it dispatches
/// exactly one AgentRun via ITaskDispatchScheduler.DispatchWorkItemAsync and
/// records the linkage. All build/test/repair/review/evaluation/learning and
/// terminal-status determination now live in the AgentRun pipeline
/// (AgentRunWorker/AgentOrchestrator/AgentRunWorkItemSync/VerificationGate/
/// WorkItemCompletionProcessor); OrchestrationService projects the live
/// WorkItem/AgentRun state back onto this run at read time.
/// </summary>
public sealed class OrchestrationRunExecutionEngine
{
    private readonly IProjectRepository _projectRepository;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly ITaskDispatchScheduler _scheduler;
    private readonly ILogger<OrchestrationRunExecutionEngine> _logger;

    public OrchestrationRunExecutionEngine(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        IOrchestrationRunRepository runRepository,
        ITaskDispatchScheduler scheduler,
        ILogger<OrchestrationRunExecutionEngine> logger)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _runRepository = runRepository;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task ExecuteAsync(OrchestrationRun run, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(run, cancellationToken);
            return;
        }

        var project = await _projectRepository.GetByIdAsync(run.ProjectId, cancellationToken);
        var workItem = await _workItemRepository.GetByIdAsync(run.WorkItemId, cancellationToken);

        if (project is null || workItem is null)
        {
            await FailAsync(run, OrchestrationFailureCategory.InfrastructureFailure, "Project or task no longer exists.", cancellationToken);
            return;
        }

        run.Status = OrchestrationRunStatus.Running;
        run.StartedAtUtc = DateTime.UtcNow;
        await _runRepository.UpdateAsync(run, cancellationToken);

        TaskDispatchOutcome outcome;

        try
        {
            outcome = await _scheduler.DispatchWorkItemAsync(workItem.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(run, cancellationToken);
            return;
        }

        if (!outcome.Dispatched)
        {
            await FailAsync(
                run, OrchestrationFailureCategory.InfrastructureFailure,
                outcome.SkipDetail ?? "WorkItem could not be dispatched.", cancellationToken);
            return;
        }

        run.AgentRunId = outcome.AgentRunId;
        run.Status = OrchestrationRunStatus.Executing;
        await _runRepository.UpdateAsync(run, cancellationToken);
        await ReportAsync(run, OrchestrationProgressStage.AgentStarted, $"Dispatched to AgentRun '{outcome.AgentRunId}'.", cancellationToken);
    }

    private async Task FailAsync(
        OrchestrationRun run, OrchestrationFailureCategory category, string errorMessage, CancellationToken cancellationToken)
    {
        run.Status = OrchestrationRunStatus.Failed;
        run.FailureCategory = category;
        run.ErrorMessage = errorMessage;
        run.CompletedAtUtc = DateTime.UtcNow;

        _logger.LogWarning("OrchestrationRun {RunId} failed: {Category} — {Error}", run.Id, category, errorMessage);

        await _runRepository.UpdateAsync(run, cancellationToken);
        await ReportAsync(run, OrchestrationProgressStage.Failed, errorMessage, CancellationToken.None);
    }

    private async Task CancelAsync(OrchestrationRun run, CancellationToken cancellationToken)
    {
        run.Status = OrchestrationRunStatus.Cancelled;
        run.FailureCategory = OrchestrationFailureCategory.Cancelled;
        run.ErrorMessage = "Execution was cancelled.";
        run.CompletedAtUtc = DateTime.UtcNow;

        // Persisting the cancellation itself must not honor the very token
        // that was just cancelled.
        await _runRepository.UpdateAsync(run, CancellationToken.None);
        await ReportAsync(run, OrchestrationProgressStage.Failed, "Execution was cancelled.", CancellationToken.None);
    }

    private async Task ReportAsync(
        OrchestrationRun run, OrchestrationProgressStage stage, string message, CancellationToken cancellationToken)
    {
        await _runRepository.AddProgressEventAsync(new OrchestrationProgressEvent
        {
            Id = Guid.NewGuid(),
            OrchestrationRunId = run.Id,
            Stage = stage,
            TimestampUtc = DateTime.UtcNow,
            Message = message
        }, cancellationToken);
    }
}
