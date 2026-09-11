using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// AgentRun -> WorkItem completion mapping (Phase 18.3 §10, extended Phase
/// 18.7 §7). Runtime-level outcomes (Failed/TimedOut/Cancelled) stay a plain
/// status map, unchanged since Phase 18.3. A Succeeded AgentRun no longer
/// blindly means WorkItem.Completed: its StructuredResult is interpreted
/// through <see cref="IWorkItemExecutionResultMapper"/> first, because "a
/// successful LLM process must not automatically mean successful software
/// delivery" (Phase 18.7 §7). No Planner/Coder/Tester/Reviewer-specific
/// behavior beyond that one deterministic policy — still just the existing
/// WorkItemTransitions guard underneath.
/// </summary>
public sealed class AgentRunWorkItemSync : IAgentRunWorkItemSync
{
    private readonly IWorkItemRepository _workItems;
    private readonly IWorkItemExecutionResultMapper _executionResultMapper;
    private readonly ILogger<AgentRunWorkItemSync> _logger;

    public AgentRunWorkItemSync(
        IWorkItemRepository workItems,
        IWorkItemExecutionResultMapper executionResultMapper,
        ILogger<AgentRunWorkItemSync> logger)
    {
        _workItems = workItems;
        _executionResultMapper = executionResultMapper;
        _logger = logger;
    }

    public async Task SyncAsync(AgentRun agentRun, CancellationToken cancellationToken = default)
    {
        if (agentRun.WorkItemId is not { } workItemId)
        {
            return;
        }

        if (!AgentRunTransitions.IsTerminal(agentRun.Status))
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
            // This is also this method's idempotency guard (Phase 18.7
            // §11): a second observation of the same terminal AgentRun finds
            // the WorkItem no longer Running and returns here without
            // re-transitioning anything.
            _logger.LogInformation(
                "AgentRunWorkItemSyncSkipped AgentRunId={AgentRunId} WorkItemId={WorkItemId} Reason=NotRunning CurrentStatus={CurrentStatus}",
                agentRun.Id, workItemId, workItem.Status);
            return;
        }

        var (target, errorMessage) = DetermineTarget(agentRun);

        if (target is null)
        {
            // Defensive — AgentRunTransitions.IsTerminal(agentRun.Status)
            // already guarantees a mapped target for every terminal status.
            return;
        }

        if (target != WorkItemStatus.Completed)
        {
            workItem.CurrentError = errorMessage;
        }

        try
        {
            workItem.TransitionTo(target.Value);
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
    /// Runtime failure (Phase 18.7 §12 "Runtime failure"): Failed -> Failed,
    /// Cancelled -> Cancelled. TimedOut has no dedicated WorkItemStatus (see
    /// WorkItemStatus enum) — documented policy: mapped to Failed, same as a
    /// plain failure, with CurrentError recording that it was specifically a
    /// timeout. Succeeded is delegated to <see cref="DetermineSucceededTarget"/>.
    /// </summary>
    private (WorkItemStatus? Target, string? ErrorMessage) DetermineTarget(AgentRun agentRun) => agentRun.Status switch
    {
        AgentRunStatus.Succeeded => DetermineSucceededTarget(agentRun),
        AgentRunStatus.TimedOut => (WorkItemStatus.Failed, agentRun.ErrorMessage
            ?? "AgentRun timed out — no WorkItemStatus.TimedOut exists; mapped to Failed."),
        AgentRunStatus.Failed => (WorkItemStatus.Failed, agentRun.ErrorMessage
            ?? $"AgentRun '{agentRun.Id}' ended in status '{agentRun.Status}'."),
        AgentRunStatus.Cancelled => (WorkItemStatus.Cancelled, agentRun.ErrorMessage
            ?? $"AgentRun '{agentRun.Id}' ended in status '{agentRun.Status}'."),
        _ => (null, null)
    };

    /// <summary>
    /// Phase 18.7 §7/§12 — a Succeeded AgentRun is interpreted, not trusted
    /// blindly:
    /// <list type="bullet">
    /// <item>missing/unparseable/Invalid StructuredResult -> "Execution
    /// result invalid" -> WorkItem.Failed (cannot safely claim any better
    /// state for an untrustworthy report).</item>
    /// <item>ExecutionSucceeded -> WorkItem.Completed. WorkItemStatus.Testing
    /// was considered as an intermediate stop instead, but
    /// ITaskDependencyResolver.Classify (unmodified, §3/§20 forbid a second
    /// dependency resolver) only ever treats a dependency as satisfied when
    /// its target is exactly Completed — routing through Testing would
    /// permanently strand every dependent WorkItem, breaking "existing
    /// dependency/readiness behavior remains correct" (§13/§23). The "not
    /// automatically" principle is instead satisfied by what already
    /// happened above: Completed is now reachable only through a genuine,
    /// deterministically-validated "completed" execution report, not merely
    /// because the AgentRun process exited successfully.</item>
    /// <item>ExecutionRequestedRepair -> WorkItem.Blocked — Running has no
    /// direct transition to Repairing in the existing WorkItemTransitions
    /// table, but Blocked is reachable from Running and, unlike Failed, can
    /// return to Ready — the smallest state compatible with "needs further
    /// work, not a terminal failure".</item>
    /// </list>
    /// </summary>
    private (WorkItemStatus? Target, string? ErrorMessage) DetermineSucceededTarget(AgentRun agentRun)
    {
        var structuredResult = TryDeserializeStructuredResult(agentRun.StructuredResult);

        if (structuredResult is null || structuredResult.Status == StructuredResultStatus.Invalid)
        {
            _logger.LogWarning(
                "WorkItemExecutionResultInvalid AgentRunId={AgentRunId} Reason=NoValidStructuredResult", agentRun.Id);
            return (WorkItemStatus.Failed, "AgentRun succeeded but produced no valid structured execution result.");
        }

        WorkItemExecutionResult mapped;

        try
        {
            mapped = _executionResultMapper.Map(structuredResult);
        }
        catch (WorkItemExecutionResultInvalidException ex)
        {
            _logger.LogWarning(
                "WorkItemExecutionResultInvalid AgentRunId={AgentRunId} Reason={Reason}", agentRun.Id, ex.Message);
            return (WorkItemStatus.Failed, $"Execution result could not be interpreted: {ex.Message}");
        }

        return mapped.Outcome switch
        {
            WorkItemExecutionResultOutcome.ExecutionSucceeded => (WorkItemStatus.Completed, null),
            WorkItemExecutionResultOutcome.ExecutionRequestedRepair =>
                (WorkItemStatus.Blocked, mapped.Summary ?? "Agent execution requested repair."),
            _ => (WorkItemStatus.Failed, "Unrecognized execution outcome.")
        };
    }

    private static StructuredAgentResult? TryDeserializeStructuredResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StructuredAgentResult>(json, StructuredAgentResult.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
