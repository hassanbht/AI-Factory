namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Assembled whenever validation fails. Not currently fed back into the
/// next attempt's prompt (that would be a further design decision beyond
/// this data-assembly requirement) — it is built and logged for
/// observability and as the basis for a future repair-feedback loop.
/// </summary>
public sealed record RepairContext(
    Guid WorkItemId,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CompilerErrors,
    IReadOnlyList<string> TestFailures,
    string? RelevantSource,
    string? PreviousAttemptOutput,
    int PreviousRepairAttempts);

/// <summary>Policy applied once a WorkItem's repair attempts are exhausted.</summary>
public enum RepairExhaustionPolicy
{
    Failed,
    HumanReview
}
