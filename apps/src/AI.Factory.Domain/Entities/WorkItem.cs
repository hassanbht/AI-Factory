using AI.Factory.Domain.Common;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;

namespace AI.Factory.Domain.Entities;

public sealed class WorkItem : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid? FeatureId { get; set; }

    public Guid? ParentWorkItemId { get; set; }

    public WorkItemType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "Draft";

    public string Priority { get; set; } = "Normal";

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public string? AcceptanceCriteria { get; set; }

    public string? CurrentError { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Proof of liveness during a long-running attempt — refreshed at each major pipeline stage, not on a literal timer.</summary>
    public DateTime? LastHeartbeatAtUtc { get; set; }

    /// <summary>Label of the last successfully completed stage of the current attempt (e.g. "AgentExecutionCompleted", "ValidationPassed") — lets recovery see how far an interrupted attempt actually got.</summary>
    public string? LastCheckpoint { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public long Version { get; set; } = 1;

    public Project? Project { get; set; }

    public Feature? Feature { get; set; }

    public WorkItem? ParentWorkItem { get; set; }

    public ICollection<WorkItemDependency> Dependencies { get; set; }
        = new List<WorkItemDependency>();

    public ICollection<HermesExecution> HermesExecutions { get; set; }
        = new List<HermesExecution>();

    public ICollection<ValidationRun> ValidationRuns { get; set; }
        = new List<ValidationRun>();

    public ICollection<ReviewRun> ReviewRuns { get; set; }
        = new List<ReviewRun>();

    public ICollection<EvaluationRun> EvaluationRuns { get; set; }
        = new List<EvaluationRun>();

    /// <summary>
    /// Applies a lifecycle transition through <see cref="WorkItemTransitions"/>,
    /// throwing <see cref="InvalidWorkItemTransitionException"/> if not allowed.
    /// Existing code that assigns <see cref="Status"/> directly (e.g. TaskStateManager)
    /// is untouched — this is the new, guarded path for new callers.
    /// </summary>
    public void TransitionTo(WorkItemStatus next)
    {
        var current = Enum.TryParse<WorkItemStatus>(Status, out var parsed) ? parsed : (WorkItemStatus?)null;

        if (current is null || !WorkItemTransitions.IsValid(current.Value, next))
        {
            throw new InvalidWorkItemTransitionException(current ?? WorkItemStatus.Draft, next);
        }

        Status = next.ToString();
        UpdatedAtUtc = DateTime.UtcNow;

        if (next == WorkItemStatus.Running && StartedAtUtc is null)
        {
            StartedAtUtc = DateTime.UtcNow;
        }

        if (WorkItemTransitions.IsTerminal(next))
        {
            CompletedAtUtc = DateTime.UtcNow;
        }
    }
}
