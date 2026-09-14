using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.13 — OrchestrationRun is a compatibility facade with no
/// execution authority of its own: StartAsync still only creates the Pending
/// row (dispatch happens later, in OrchestrationRunExecutionEngine via the
/// Worker), but GetAsync/RequestCancellationAsync now project the live
/// WorkItem/AgentRun state back onto the run at read time once it has
/// dispatched, and cancellation operates on the WorkItem — the same single
/// authority every other cancellation path in the system already uses.
/// </summary>
public sealed class OrchestrationService : IOrchestrationService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IWorkItemRepository _workItemRepository;
    private readonly IOrchestrationRunRepository _runRepository;
    private readonly IAgentRunRepository _agentRunRepository;
    private readonly OrchestrationOptions _options;

    public OrchestrationService(
        IProjectRepository projectRepository,
        IWorkItemRepository workItemRepository,
        IOrchestrationRunRepository runRepository,
        IAgentRunRepository agentRunRepository,
        OrchestrationOptions options)
    {
        _projectRepository = projectRepository;
        _workItemRepository = workItemRepository;
        _runRepository = runRepository;
        _agentRunRepository = agentRunRepository;
        _options = options;
    }

    public async Task<OrchestrationRunResult> StartAsync(
        StartOrchestrationCommand command,
        CancellationToken cancellationToken = default)
    {
        var project = await _projectRepository.GetByIdAsync(command.ProjectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{command.ProjectId}' was not found.");

        var workItem = await _workItemRepository.GetByIdAsync(command.TaskId, cancellationToken)
            ?? throw new NotFoundException($"Task '{command.TaskId}' was not found.");

        if (workItem.ProjectId != project.Id)
        {
            throw new ValidationException($"Task '{command.TaskId}' does not belong to project '{command.ProjectId}'.");
        }

        var run = new OrchestrationRun
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkItemId = workItem.Id,
            Status = OrchestrationRunStatus.Pending,
            CurrentStage = OrchestrationProgressStage.RunCreated,
            RequestedAtUtc = DateTime.UtcNow,
            MaxAttempts = command.MaxAttempts ?? _options.MaxAttempts
        };

        var created = await _runRepository.CreateAsync(run, cancellationToken);

        await _runRepository.AddProgressEventAsync(new OrchestrationProgressEvent
        {
            Id = Guid.NewGuid(),
            OrchestrationRunId = created.Id,
            Stage = OrchestrationProgressStage.RunCreated,
            TimestampUtc = DateTime.UtcNow,
            Message = $"Run created for task '{workItem.Title}'."
        }, cancellationToken);

        return new OrchestrationRunResult(
            created.Id,
            created.Status.ToString(),
            $"/api/orchestration-runs/{created.Id}");
    }

    public Task<OrchestrationRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        LoadWithLiveProjectionAsync(runId, cancellationToken);

    public async Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await LoadWithLiveProjectionAsync(runId, cancellationToken)
            ?? throw new NotFoundException($"OrchestrationRun '{runId}' was not found.");

        if (run.Status is OrchestrationRunStatus.Completed or OrchestrationRunStatus.Failed or OrchestrationRunStatus.Cancelled)
        {
            throw new ConflictException($"OrchestrationRun '{runId}' is already terminal ({run.Status}) and cannot be cancelled.");
        }

        await _runRepository.RequestCancellationAsync(runId, cancellationToken);
        await CancelWorkItemCooperativelyAsync(run.WorkItemId, cancellationToken);
    }

    /// <summary>
    /// Phase 18.13 — OrchestrationRun is a compatibility facade with no
    /// execution authority of its own once it has dispatched into the
    /// AgentRun pipeline: everything below is a read-time PROJECTION of the
    /// authoritative WorkItem/AgentRun state onto the tracked-but-not-saved
    /// `run` object returned to the caller. Nothing here calls UpdateAsync —
    /// this must never persist, only reflect.
    /// </summary>
    private async Task<OrchestrationRun?> LoadWithLiveProjectionAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await _runRepository.GetByIdAsync(runId, cancellationToken);

        if (run is null || run.AgentRunId is not { } agentRunId
            || run.Status is OrchestrationRunStatus.Completed or OrchestrationRunStatus.Failed or OrchestrationRunStatus.Cancelled)
        {
            return run;
        }

        var workItem = await _workItemRepository.GetByIdAsync(run.WorkItemId, cancellationToken);

        if (workItem is null || !Enum.TryParse<WorkItemStatus>(workItem.Status, out var workItemStatus))
        {
            return run;
        }

        var agentRun = await _agentRunRepository.GetByIdAsync(agentRunId, cancellationToken);

        switch (workItemStatus)
        {
            case WorkItemStatus.Completed:
                run.Status = OrchestrationRunStatus.Completed;
                run.CompletedAtUtc ??= agentRun?.UpdatedAtUtc ?? DateTime.UtcNow;
                break;
            case WorkItemStatus.Failed:
                run.Status = OrchestrationRunStatus.Failed;
                run.ErrorMessage = workItem.CurrentError ?? agentRun?.ErrorMessage;
                run.CompletedAtUtc ??= agentRun?.UpdatedAtUtc ?? DateTime.UtcNow;
                break;
            case WorkItemStatus.Cancelled:
                run.Status = OrchestrationRunStatus.Cancelled;
                run.ErrorMessage ??= workItem.CurrentError ?? "Execution was cancelled.";
                run.CompletedAtUtc ??= agentRun?.UpdatedAtUtc ?? DateTime.UtcNow;
                break;
            case WorkItemStatus.Repairing:
                run.Status = OrchestrationRunStatus.Repairing;
                break;
            default:
                run.Status = OrchestrationRunStatus.Executing;
                break;
        }

        return run;
    }

    /// <summary>
    /// Phase 18.13 — cancellation's single authority is WorkItemTransitions
    /// (unchanged): transitioning the WorkItem straight to Cancelled excludes
    /// it from every future TaskDispatchScheduler cycle, and
    /// AgentRunWorkItemSync's own idempotency guard (WorkItem must still be
    /// Running) means a still-in-flight AgentRun that finishes afterward can
    /// never resurrect or overwrite this terminal state. Cooperative, not
    /// preemptive — matches the legacy engine's own semantics (it never
    /// actually interrupted an in-flight Hermes call either, only prevented
    /// the NEXT attempt).
    /// </summary>
    private async Task CancelWorkItemCooperativelyAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await _workItemRepository.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null
            || !Enum.TryParse<WorkItemStatus>(workItem.Status, out var current)
            || WorkItemTransitions.IsTerminal(current))
        {
            return;
        }

        try
        {
            workItem.TransitionTo(WorkItemStatus.Cancelled);
            await _workItemRepository.UpdateAsync(workItem, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Lost the race against a concurrent completion/failure — that
            // outcome wins; cancellation here is cooperative, not guaranteed.
        }
    }
}
