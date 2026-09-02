namespace AI.Factory.Application.Abstractions;

public interface IAutonomousCodingOrchestrator
{
    /// <summary>
    /// Runs the full Ready→Complete-or-Repair loop for the single next
    /// ready task in the given project. Returns null if nothing is ready
    /// (not an error — just nothing to do right now).
    /// </summary>
    Task<WorkItemExecutionOutcome?> ExecuteNextReadyTaskAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs exactly one attempt of the same pipeline for a SPECIFIC
    /// WorkItem, regardless of scheduler ordering. Throws NotFoundException
    /// if the WorkItem does not exist. A caller driving a full repair loop
    /// (see OrchestrationRunExecutionEngine) calls this repeatedly until
    /// the WorkItem reaches a terminal status.
    /// </summary>
    Task<WorkItemExecutionOutcome?> ExecuteSpecificWorkItemAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default);
}

public sealed record WorkItemExecutionOutcome(
    Guid WorkItemId,
    bool Success,
    string? Error,
    PipelineFailureCategory? Category = null);
