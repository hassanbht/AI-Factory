using AI.Factory.Domain.Common;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;

namespace AI.Factory.Domain.Entities;

public sealed class Project : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Created;

    public string RootPath { get; set; } = string.Empty;

    public string? GitRepository { get; set; }

    public string? GitBranch { get; set; }

    /// <summary>
    /// Phase 18.10 — bounded reason the Project last landed in a non-happy
    /// lifecycle state (e.g. a Project-level verification failure summary).
    /// Mirrors WorkItem.CurrentError; never unbounded raw process output.
    /// </summary>
    public string? CurrentError { get; set; }

    /// <summary>
    /// Phase 18.25 — the authoritative, durable marker that autonomous
    /// lifecycle execution has been started for this Project. Null means the
    /// Project is operator-driven only: the lifecycle orchestrator never
    /// touches it. Deliberately a separate field rather than an inference
    /// from <see cref="Status"/>, because a status alone cannot distinguish
    /// "the Factory owns this Project's progression" from "an operator is
    /// walking it through by hand" (spec 18.25 §3/§7).
    /// </summary>
    public DateTime? LifecycleStartedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public long Version { get; set; } = 1;

    /// <summary>Applies a lifecycle transition, throwing <see cref="InvalidProjectTransitionException"/> if not allowed.</summary>
    public void TransitionTo(ProjectStatus next)
    {
        if (!ProjectTransitions.IsValid(Status, next))
        {
            throw new InvalidProjectTransitionException(Status, next);
        }

        Status = next;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
