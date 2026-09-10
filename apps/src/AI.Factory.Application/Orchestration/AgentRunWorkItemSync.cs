using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Generic AgentRun -> WorkItem completion mapping (Phase 18.3 §10). Stays
/// deliberately dumb: no Planner/Coder/Tester/Reviewer-specific behavior,
/// just a status map plus the existing WorkItemTransitions guard.
/// </summary>
public sealed class AgentRunWorkItemSync : IAgentRunWorkItemSync
{
    private readonly IWorkItemRepository _workItems;
    private readonly ILogger<AgentRunWorkItemSync> _logger;

    public AgentRunWorkItemSync(IWorkItemRepository workItems, ILogger<AgentRunWorkItemSync> logger)
    {
        _workItems = workItems;
        _logger = logger;
    }

    public async Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default)
    {
        if (agentRun.WorkItemId is not { } workItemId)
        {
            return;
        }

        if (!TryMapTarget(agentRun.Status, out var target))
        {
            // Non-terminal AgentRun status — nothing to sync yet.
            return;
        }

        var workItem = await _workItems.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            _logger.LogWarning(
                "AgentRunWorkItemSyncSkipped AgentRunId={AgentRunId} WorkItemId={WorkItemId} Reason=WorkItemNotFound",
                agentRun.Id, workItemId);
            return;
        }

        if (!Enum.TryParse<WorkItemStatus>(workItem.Status, out var current) || current != WorkItemStatus.Running)
        {
            // Already moved on independently (e.g. terminal via another
            // path, or never actually Running) — never overwrite it here.
            _logger.LogInformation(
                "AgentRunWorkItemSyncSkipped AgentRunId={AgentRunId} WorkItemId={WorkItemId} Reason=NotRunning CurrentStatus={CurrentStatus}",
                agentRun.Id, workItemId, workItem.Status);
            return;
        }

        if (target != WorkItemStatus.Completed)
        {
            workItem.CurrentError = agentRun.ErrorMessage
                ?? (agentRun.Status == AgentRunStatus.TimedOut
                    ? "AgentRun timed out — no WorkItemStatus.TimedOut exists; mapped to Failed."
                    : $"AgentRun '{agentRun.Id}' ended in status '{agentRun.Status}'.");
        }

        try
        {
            workItem.TransitionTo(target);
            await _workItems.UpdateAsync(workItem, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Lost a race with an independent WorkItem update — the other
            // writer's outcome wins; do not retry/overwrite.
            _logger.LogInformation(
                "AgentRunWorkItemSyncLostRace AgentRunId={AgentRunId} WorkItemId={WorkItemId}", agentRun.Id, workItemId);
            return;
        }

        _logger.LogInformation(
            "AgentRunWorkItemSynced AgentRunId={AgentRunId} WorkItemId={WorkItemId} AgentRunStatus={AgentRunStatus} WorkItemStatus={WorkItemStatus}",
            agentRun.Id, workItemId, agentRun.Status, target);
    }

    /// <summary>
    /// Succeeded -> Completed, Failed -> Failed, Cancelled -> Cancelled.
    /// TimedOut has no dedicated WorkItemStatus (see WorkItemStatus enum) —
    /// documented policy: mapped to Failed, same as a plain failure, with
    /// CurrentError recording that it was specifically a timeout.
    /// </summary>
    private static bool TryMapTarget(AgentRunStatus status, out WorkItemStatus target)
    {
        switch (status)
        {
            case AgentRunStatus.Succeeded:
                target = WorkItemStatus.Completed;
                return true;
            case AgentRunStatus.Failed:
            case AgentRunStatus.TimedOut:
                target = WorkItemStatus.Failed;
                return true;
            case AgentRunStatus.Cancelled:
                target = WorkItemStatus.Cancelled;
                return true;
            default:
                target = default;
                return false;
        }
    }
}
