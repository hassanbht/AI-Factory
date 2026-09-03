using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Drives one claimed OrchestrationRun end to end. Deliberately thin:
/// planning + workspace preparation are the only genuinely new logic here
/// — everything from model selection through agent execution, build, test,
/// repair, review, evaluation, learning, and approval is delegated to the
/// existing, already-tested IAutonomousCodingOrchestrator.ExecuteSpecificWorkItemAsync,
/// called once per attempt, exactly like the autonomous scheduler path
/// already does for ExecuteNextReadyTaskAsync.
/// </summary>
public sealed class OrchestrationRunExecutionEngine
{
    private static readonly string[] ExcludedDirectoryNames = { "bin", "obj", "node_modules", ".git", ".vs" };

    private readonly IProjectRepository _projectRepository;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly ITaskPlanner _taskPlanner;
    private readonly IAutonomousCodingOrchestrator _orchestrator;
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<OrchestrationRunExecutionEngine> _logger;

    public OrchestrationRunExecutionEngine(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        ITaskPlanner taskPlanner,
        IAutonomousCodingOrchestrator orchestrator,
        IOrchestrationRunRepository runRepository,
        OrchestrationOptions options,
        ILogger<OrchestrationRunExecutionEngine> logger)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _taskPlanner = taskPlanner;
        _orchestrator = orchestrator;
        _runRepository = runRepository;
        _options = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(OrchestrationRun run, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(run, cancellationToken);
            return;
        }

        Project? project;
        WorkItem? workItem;

        try
        {
            project = await _projectRepository.GetByIdAsync(run.ProjectId, cancellationToken);
            workItem = await _workItemRepository.GetByIdAsync(run.WorkItemId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(run, cancellationToken);
            return;
        }

        if (project is null || workItem is null)
        {
            await FailAsync(run, OrchestrationFailureCategory.InfrastructureFailure,
                "Project or task no longer exists.", cancellationToken);
            return;
        }

        run.Status = OrchestrationRunStatus.Running;
        run.StartedAtUtc = DateTime.UtcNow;
        await _runRepository.UpdateAsync(run, cancellationToken);

        await StartStepAsync(run, OrchestrationStepKind.WorkspacePreparation, cancellationToken);

        try
        {
            PrepareWorkspace(run, project);
        }
        catch (Exception ex)
        {
            await FailStepAsync(run, OrchestrationStepKind.WorkspacePreparation, ex.GetType().Name, ex.Message, cancellationToken);
            throw;
        }

        await CompleteStepAsync(run, OrchestrationStepKind.WorkspacePreparation, null, cancellationToken);
        await _runRepository.UpdateAsync(run, cancellationToken);
        await ReportAsync(run, OrchestrationProgressStage.ContextRetrieved, "Workspace prepared.", cancellationToken);

        run.Status = OrchestrationRunStatus.Planning;
        await _runRepository.UpdateAsync(run, cancellationToken);
        await ReportAsync(run, OrchestrationProgressStage.PlanningStarted, "Planning started.", cancellationToken);
        await StartStepAsync(run, OrchestrationStepKind.TaskPlanning, cancellationToken);

        TaskPlan plan;

        try
        {
            plan = await _taskPlanner.CreatePlanAsync(
                new TaskPlanRequest(project.Id, workItem.Title, BuildSpecificationContent(workItem)),
                cancellationToken);

            TaskPlanValidator.Validate(plan);
        }
        catch (OperationCanceledException)
        {
            await FailStepAsync(run, OrchestrationStepKind.TaskPlanning, "Cancelled", "Operation was cancelled.", cancellationToken);
            await CancelAsync(run, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is TaskPlanningException or InvalidTaskPlanException)
        {
            // Do not call Hermes — planning/validation failed outright.
            await FailStepAsync(run, OrchestrationStepKind.TaskPlanning, ex.GetType().Name, ex.Message, cancellationToken);
            await FailAsync(run, OrchestrationFailureCategory.PlanningFailure, ex.Message, cancellationToken);
            return;
        }

        await CompleteStepAsync(
            run, OrchestrationStepKind.TaskPlanning,
            new Dictionary<string, string> { ["stepCount"] = plan.Tasks.Count.ToString() }, cancellationToken);

        await ReportAsync(
            run, OrchestrationProgressStage.PlanReady, $"Plan ready with {plan.Tasks.Count} step(s).", cancellationToken);

        // Makes the plan part of the next agent prompt via the EXISTING,
        // unchanged BuildPrompt (which already includes workItem.Description)
        // — no need to touch AutonomousCodingOrchestrator's prompt assembly.
        workItem.Description += "\n\n=== Task Plan ===\n" + FormatPlan(plan);
        await _workItemRepository.UpdateAsync(workItem, cancellationToken);

        run.Status = OrchestrationRunStatus.Executing;
        await _runRepository.UpdateAsync(run, cancellationToken);

        await RunAttemptLoopAsync(run, workItem, cancellationToken);
    }

    private async Task RunAttemptLoopAsync(OrchestrationRun run, WorkItem workItem, CancellationToken cancellationToken)
    {
        while (true)
        {
            var freshRun = await _runRepository.GetByIdAsync(run.Id, cancellationToken);

            if (cancellationToken.IsCancellationRequested || freshRun?.CancellationRequestedAtUtc is not null)
            {
                await CancelAsync(run, cancellationToken);
                return;
            }

            var attemptNumber = workItem.AttemptCount + 1;
            var attemptStartedAtUtc = DateTime.UtcNow;

            WorkItemExecutionOutcome? outcome;

            try
            {
                outcome = await _orchestrator.ExecuteSpecificWorkItemAsync(workItem.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await CancelAsync(run, cancellationToken);
                return;
            }

            workItem = await _workItemRepository.GetByIdAsync(workItem.Id, cancellationToken)
                ?? throw new InvalidOperationException($"WorkItem '{workItem.Id}' vanished mid-execution.");

            run.Attempt = workItem.AttemptCount;

            await _runRepository.AddAttemptAsync(new OrchestrationRunAttempt
            {
                Id = Guid.NewGuid(),
                OrchestrationRunId = run.Id,
                AttemptNumber = attemptNumber,
                StartedAtUtc = attemptStartedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                Success = outcome?.Success ?? false,
                ErrorMessage = outcome?.Error,
                FailureCategory = MapFailureCategory(outcome?.Category)
            }, cancellationToken);

            if (workItem.Status == WorkItemStatus.Completed.ToString())
            {
                await CompleteAsync(run, workItem, cancellationToken);
                return;
            }

            if (workItem.Status == WorkItemStatus.Failed.ToString())
            {
                await FailAsync(
                    run,
                    MapFailureCategory(outcome?.Category) ?? OrchestrationFailureCategory.AgentFailure,
                    outcome?.Error ?? workItem.CurrentError ?? "Task failed after exhausting all attempts.",
                    cancellationToken);
                return;
            }

            // Still Repairing — the existing per-attempt call already fed
            // this attempt's failure into WorkItem.CurrentError/AgentRun/
            // ValidationRun, which BuildRepairContextTextAsync reads on the
            // NEXT call — RepairContext genuinely drives the next prompt,
            // not just a log line.
            run.Status = OrchestrationRunStatus.Repairing;
            await _runRepository.UpdateAsync(run, cancellationToken);
            await ReportAsync(
                run, OrchestrationProgressStage.RepairStarted,
                $"Repairing after attempt {attemptNumber}.", cancellationToken);
        }
    }

    private async Task CompleteAsync(OrchestrationRun run, WorkItem workItem, CancellationToken cancellationToken)
    {
        var project = await _projectRepository.GetByIdAsync(run.ProjectId, cancellationToken);

        if (project?.Status == ProjectStatus.HumanReview)
        {
            run.Status = OrchestrationRunStatus.AwaitingApproval;
            await ReportAsync(run, OrchestrationProgressStage.AwaitingApproval, "Awaiting human approval.", cancellationToken);
        }
        else
        {
            run.Status = OrchestrationRunStatus.Completed;
            await ReportAsync(run, OrchestrationProgressStage.Completed, "Run completed successfully.", cancellationToken);
        }

        run.CompletedAtUtc = DateTime.UtcNow;
        await _runRepository.UpdateAsync(run, cancellationToken);
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

    // The engine already holds `run` directly (unlike AutonomousCodingOrchestrator,
    // which only knows a WorkItemId and goes through IOrchestrationRunTraceService)
    // so it writes OrchestrationRunSteps straight through the repository, same as
    // its own ReportAsync above does for progress events.

    private async Task StartStepAsync(OrchestrationRun run, OrchestrationStepKind stage, CancellationToken cancellationToken)
    {
        await _runRepository.AddStepAsync(new OrchestrationRunStep
        {
            Id = Guid.NewGuid(),
            OrchestrationRunId = run.Id,
            Stage = stage,
            Status = OrchestrationStepStatus.Running,
            StartedAtUtc = DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task CompleteStepAsync(
        OrchestrationRun run, OrchestrationStepKind stage, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        var step = await _runRepository.FindLatestOpenStepAsync(run.Id, stage, attemptNumber: null, cancellationToken);

        if (step is null)
        {
            return;
        }

        step.Status = OrchestrationStepStatus.Succeeded;
        step.CompletedAtUtc = DateTime.UtcNow;

        if (metadata is not null && metadata.Count > 0)
        {
            step.MetadataJson = JsonSerializer.Serialize(metadata);
        }

        await _runRepository.UpdateStepAsync(step, cancellationToken);
    }

    private async Task FailStepAsync(
        OrchestrationRun run, OrchestrationStepKind stage, string failureType, string failureMessage, CancellationToken cancellationToken)
    {
        var step = await _runRepository.FindLatestOpenStepAsync(run.Id, stage, attemptNumber: null, cancellationToken);

        if (step is null)
        {
            return;
        }

        step.Status = OrchestrationStepStatus.Failed;
        step.CompletedAtUtc = DateTime.UtcNow;
        step.FailureType = failureType;
        step.FailureMessage = failureMessage;

        await _runRepository.UpdateStepAsync(step, cancellationToken);
    }

    private void PrepareWorkspace(OrchestrationRun run, Project project)
    {
        var workspacePath = WorkspacePathGuard.EnsureWithinRoot(
            _options.WorkspaceRoot, Path.Combine("runs", run.Id.ToString("N"), "workspace"));

        Directory.CreateDirectory(workspacePath);

        if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
        {
            CopyDirectory(project.RootPath, workspacePath);
        }

        run.WorkspacePath = workspacePath;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (ExcludedDirectoryNames.Any(excluded =>
                filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Contains(excluded, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var destinationPath = Path.Combine(destinationDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private static string BuildSpecificationContent(WorkItem workItem)
    {
        var acceptance = string.IsNullOrWhiteSpace(workItem.AcceptanceCriteria)
            ? string.Empty
            : $"\n\nAcceptance Criteria:\n{workItem.AcceptanceCriteria}";

        return $"{workItem.Description}{acceptance}";
    }

    private static string FormatPlan(TaskPlan plan)
    {
        return string.Join(
            "\n",
            plan.Tasks.Select(t => $"- [{t.Type}/{t.EstimatedComplexity}] {t.Title}: {t.Description}"));
    }

    private static OrchestrationFailureCategory? MapFailureCategory(PipelineFailureCategory? category)
    {
        return category switch
        {
            PipelineFailureCategory.AgentExecution => OrchestrationFailureCategory.AgentFailure,
            PipelineFailureCategory.Build => OrchestrationFailureCategory.BuildFailure,
            PipelineFailureCategory.Test => OrchestrationFailureCategory.TestFailure,
            PipelineFailureCategory.Validation => OrchestrationFailureCategory.ValidationFailure,
            PipelineFailureCategory.Review => OrchestrationFailureCategory.ReviewFailure,
            PipelineFailureCategory.Repair => OrchestrationFailureCategory.InfrastructureFailure,
            PipelineFailureCategory.Infrastructure => OrchestrationFailureCategory.InfrastructureFailure,
            PipelineFailureCategory.HumanRejection => OrchestrationFailureCategory.InfrastructureFailure,
            null => null,
            _ => OrchestrationFailureCategory.InfrastructureFailure
        };
    }
}
