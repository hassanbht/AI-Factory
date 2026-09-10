namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.3 §15 — minimal in-memory scheduler operational visibility, same
/// spirit as IOperationalMetrics (live snapshot, resets on restart, no
/// persisted history) but scoped to "what did the scheduler do last / in total".
/// </summary>
public interface ISchedulerStatusTracker
{
    void RecordCycle(SchedulingCycleResult result);

    SchedulerStatusSnapshot GetSnapshot();
}

public sealed record SchedulerStatusSnapshot(
    bool Enabled,
    int MaxConcurrentTasks,
    DateTime? LastCycleAtUtc,
    Guid? LastCycleProjectId,
    int LastCycleDispatchedCount,
    int LastCycleFailureCount,
    int LastCycleSkippedDueToConcurrency,
    long TotalDispatched,
    long TotalFailures);
