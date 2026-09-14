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
    private readonly IProjectRepository _projects;
    private readonly IWorkItemExecutionResultMapper _executionResultMapper;
    private readonly IVerificationGate _verificationGate;
    private readonly IWorkItemCompletionProcessor _completionProcessor;
    private readonly ILogger<AgentRunWorkItemSync> _logger;

    public AgentRunWorkItemSync(
        IWorkItemRepository workItems,
        IProjectRepository projects,
        IWorkItemExecutionResultMapper executionResultMapper,
        IVerificationGate verificationGate,
        IWorkItemCompletionProcessor completionProcessor,
        ILogger<AgentRunWorkItemSync> logger)
    {
        _workItems = workItems;
        _projects = projects;
        _executionResultMapper = executionResultMapper;
        _verificationGate = verificationGate;
        _completionProcessor = completionProcessor;
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

        if (target == WorkItemStatus.Testing)
        {
            await RunVerificationGateAsync(workItem, agentRun, cancellationToken);
        }
    }

    /// <summary>
    /// Phase 18.8 — closes the gap left by Phase 18.7 §12: an agent claiming
    /// "completed" now only earns WorkItemStatus.Testing (see
    /// DetermineSucceededTarget). Runs synchronously inside the same Worker
    /// poll cycle that just persisted the Testing transition (no second
    /// polling loop, per §10/§20) and deterministically resolves Testing to
    /// its terminal outcome before returning. ITaskDependencyResolver only
    /// ever treats Completed as satisfying a dependency, so dependents stay
    /// blocked for the entire duration of this call — exactly the "Testing
    /// does not satisfy dependencies" requirement (§9).
    /// </summary>
    private async Task RunVerificationGateAsync(WorkItem workItem, AgentRun agentRun, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(workItem.ProjectId, cancellationToken);

        if (project is null)
        {
            _logger.LogWarning(
                "VerificationGateSkipped WorkItemId={WorkItemId} Reason=ProjectNotFound ProjectId={ProjectId}",
                workItem.Id, workItem.ProjectId);
            await FinalizeVerificationAsync(
                workItem, WorkItemStatus.Failed, "Verification could not run: owning Project was not found.", cancellationToken);
            return;
        }

        var result = await _verificationGate.VerifyAsync(project, workItem, cancellationToken);

        _logger.LogInformation(
            "VerificationGateCompleted WorkItemId={WorkItemId} Status={Status} DurationMs={DurationMs}",
            workItem.Id, result.Status, result.Duration.TotalMilliseconds);

        // Phase 18.8 §13 — Verification Failure Policy: Passed delegates to
        // IWorkItemCompletionProcessor (Phase 18.13 — independent review,
        // then evaluation/learning on a review pass) instead of assuming
        // Completed outright; a genuine build/test Failed -> Repairing
        // (recoverable, not terminal); InfrastructureError/TimedOut -> Failed
        // (deterministic, not a repairable code defect); Cancelled -> Cancelled.
        if (result.Status == VerificationStatus.Passed)
        {
            var decision = await _completionProcessor.ProcessAsync(project, workItem, agentRun, result, cancellationToken);
            await FinalizeVerificationAsync(workItem, decision.Target, decision.ErrorMessage, cancellationToken);
            return;
        }

        var (target, message) = result.Status switch
        {
            VerificationStatus.Failed => (WorkItemStatus.Repairing, result.Summary),
            VerificationStatus.InfrastructureError => (WorkItemStatus.Failed, result.Summary),
            VerificationStatus.TimedOut => (WorkItemStatus.Failed, result.Summary),
            VerificationStatus.Cancelled => (WorkItemStatus.Cancelled, result.Summary),
            _ => (WorkItemStatus.Failed, "Unrecognized verification outcome.")
        };

        await FinalizeVerificationAsync(workItem, target, message, cancellationToken);
    }

    private async Task FinalizeVerificationAsync(
        WorkItem workItem, WorkItemStatus target, string? errorMessage, CancellationToken cancellationToken)
    {
        if (target != WorkItemStatus.Completed)
        {
            workItem.CurrentError = errorMessage;
        }

        try
        {
            workItem.TransitionTo(target);
            await _workItems.UpdateAsync(workItem, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _logger.LogInformation(
                "VerificationGateLostRace WorkItemId={WorkItemId}", workItem.Id);
        }
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
    /// <item>ExecutionSucceeded -> WorkItem.Testing (Phase 18.8). The agent's
    /// self-reported "completed" is no longer sufficient proof by itself —
    /// SyncAsync runs the deterministic IVerificationGate immediately after
    /// persisting this transition and resolves Testing to Completed/
    /// Repairing/Failed/Cancelled before returning. ITaskDependencyResolver.Classify
    /// (unmodified, §3/§20 forbid a second dependency resolver) only ever
    /// treats a dependency as satisfied when its target is exactly Completed,
    /// so dependents correctly stay blocked for the entire Testing window.</item>
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
            WorkItemExecutionResultOutcome.ExecutionSucceeded => (WorkItemStatus.Testing, null),
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
