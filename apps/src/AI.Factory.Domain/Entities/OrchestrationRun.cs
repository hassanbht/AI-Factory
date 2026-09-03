using AI.Factory.Domain.Common;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Domain.Entities;

/// <summary>
/// One complete, API-triggered, tracked pass of the pipeline for a single
/// WorkItem — distinct from WorkItem.Status/AttemptCount, which track the
/// task's own cumulative state across whatever drives it (this run, or
/// future autonomous scheduling). RunId doubles as the correlation ID
/// threaded through structured logs (see Program.cs endpoints).
/// </summary>
public sealed class OrchestrationRun : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid WorkItemId { get; set; }

    public OrchestrationRunStatus Status { get; set; } = OrchestrationRunStatus.Pending;

    public OrchestrationProgressStage CurrentStage { get; set; } = OrchestrationProgressStage.RunCreated;

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public int Attempt { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public string? SelectedModel { get; set; }

    public string? WorkspacePath { get; set; }

    public string? ErrorMessage { get; set; }

    public OrchestrationFailureCategory? FailureCategory { get; set; }

    public DateTime? CancellationRequestedAtUtc { get; set; }

    /// <summary>Renewed periodically by the claiming Worker while executing — a stale value means the Worker died mid-run (see Section 15 crash recovery).</summary>
    public DateTime? WorkerHeartbeatAtUtc { get; set; }

    public DateTime? LeaseExpiresAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public ICollection<OrchestrationRunAttempt> Attempts { get; set; } = new List<OrchestrationRunAttempt>();

    public ICollection<OrchestrationProgressEvent> ProgressEvents { get; set; } = new List<OrchestrationProgressEvent>();

    public ICollection<OrchestrationRunStep> Steps { get; set; } = new List<OrchestrationRunStep>();
}
