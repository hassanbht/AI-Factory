using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.25 — the single authority for Project lifecycle progression.
///
/// It owns exactly the segment no other component owned: the transitions
/// between an initialized Project and the point where the already-proven
/// execution core takes over, i.e.
/// Analyzing -&gt; Specification -&gt; Architecture -&gt; Planning -&gt;
/// (real Planner) -&gt; Implementation. From Implementation onwards the
/// existing <see cref="IProjectCompletionGate"/> remains authoritative
/// (Implementation -&gt; Testing -&gt; Reviewing -&gt; HumanReview), and
/// HumanReview -&gt; Completed remains the explicit human action
/// <c>ProjectService.ApproveAsync</c>. This type never approves a Project
/// and never produces <see cref="ProjectStatus.Completed"/>.
///
/// It is not a workflow engine: it holds no state of its own, persists
/// nothing but the Project row the existing repository already owns, and
/// performs every transition through the existing validated
/// <see cref="Project.TransitionTo"/> mechanism — never by assigning
/// <see cref="Project.Status"/>.
/// </summary>
public interface IProjectLifecycleOrchestrator
{
    /// <summary>
    /// The one authoritative "start this project" operation. Validates the
    /// start prerequisites and records the durable
    /// <see cref="Project.LifecycleStartedAtUtc"/> marker. Idempotent: an
    /// already-started Project is returned unchanged, and two concurrent
    /// starts resolve to exactly one authoritative start via the existing
    /// optimistic-concurrency token.
    /// </summary>
    Task<Project> StartAsync(Guid projectId, string? actor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs at most one deterministic lifecycle step for a Project,
    /// driven periodically by the Worker. A Project that was never started
    /// (<see cref="Project.LifecycleStartedAtUtc"/> is null) is always a
    /// no-op, so operator-driven Projects are never hijacked. All state lives
    /// in the Project row, so a Worker restart simply resumes from it.
    /// </summary>
    Task<ProjectLifecycleAdvanceResult> AdvanceAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public enum ProjectLifecycleAction
{
    /// <summary>Not lifecycle-owned, terminal, or a stage another component owns — not an error.</summary>
    None,

    /// <summary>Lifecycle-owned, but the current stage is waiting on work it does not own (e.g. an in-flight Planner AgentRun).</summary>
    Waiting,

    /// <summary>A validated Project transition was performed.</summary>
    Advanced,

    /// <summary>A Planner AgentRun was dispatched through the existing IProjectPlanner path.</summary>
    PlannerStarted,

    /// <summary>The lifecycle failed closed with a persisted diagnostic — never silently advanced.</summary>
    Failed
}

public sealed record ProjectLifecycleAdvanceResult(
    ProjectLifecycleAction Action,
    ProjectStatus Status,
    string? Reason)
{
    public static ProjectLifecycleAdvanceResult None(ProjectStatus status, string? reason = null) =>
        new(ProjectLifecycleAction.None, status, reason);
}
