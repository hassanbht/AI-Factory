using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.13 — the post-verification stage for a WorkItem whose build+test
/// just passed IVerificationGate: independent code review, then (only on a
/// review pass) evaluation + lesson recording. This is NOT a second execution
/// authority — it never re-decides build/test outcome, never retries, and its
/// only output is the next WorkItemStatus for the caller (AgentRunWorkItemSync)
/// to apply through the existing WorkItemTransitions guard.
/// </summary>
public interface IWorkItemCompletionProcessor
{
    Task<WorkItemCompletionDecision> ProcessAsync(
        Project project,
        WorkItem workItem,
        AgentRun agentRun,
        VerificationResult verificationResult,
        CancellationToken cancellationToken = default);
}

/// <summary>Target must be Completed or Repairing — never any other WorkItemStatus.</summary>
public sealed record WorkItemCompletionDecision(WorkItemStatus Target, string? ErrorMessage);
