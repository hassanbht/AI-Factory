using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.10 — deterministic Project-level completion/review gate. Never
/// executes arbitrary LLM commands, never trusts AgentRun self-reports, and
/// never transitions a Project directly to Completed — only as far as
/// HumanReview (or Failed/Cancelled on a genuine verification failure).
/// Reuses the existing ProjectTransitions guard table and IVerificationGate;
/// this is the first component to look at ALL of a Project's WorkItems
/// together rather than one at a time.
/// </summary>
public interface IProjectCompletionGate
{
    /// <summary>
    /// Evaluates one Project against its currently persisted WorkItems and
    /// advances it at most one lifecycle step (Implementation -> Testing,
    /// Testing -> Reviewing/Failed/Cancelled, or Reviewing -> HumanReview) if
    /// and only if the deterministic facts for that step already hold. Safe
    /// to call repeatedly/concurrently/after restart — a status that does
    /// not match an expected precondition, or a lost optimistic-concurrency
    /// race, is a no-op, never an error.
    /// </summary>
    Task<ProjectCompletionGateResult> EvaluateAsync(
        Project project,
        IReadOnlyList<WorkItem> workItems,
        CancellationToken cancellationToken = default);
}

public enum ProjectCompletionGateAction
{
    /// <summary>Nothing changed — the Project's current status did not match a step this gate handles, or its precondition was not yet met.</summary>
    None,
    AdvancedToTesting,
    AdvancedToReviewing,
    AdvancedToHumanReview,
    Failed,
    Cancelled
}

public sealed record ProjectCompletionGateResult(ProjectCompletionGateAction Action, string? Reason);
