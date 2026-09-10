using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;

namespace AI.Factory.Application.Orchestration;

public sealed class SchedulerStatusTracker : ISchedulerStatusTracker
{
    private readonly TaskSchedulerOptions _options;
    private readonly object _lock = new();

    private DateTime? _lastCycleAtUtc;
    private Guid? _lastCycleProjectId;
    private int _lastDispatched;
    private int _lastFailures;
    private int _lastSkipped;
    private long _totalDispatched;
    private long _totalFailures;

    public SchedulerStatusTracker(TaskSchedulerOptions options)
    {
        _options = options;
    }

    public void RecordCycle(SchedulingCycleResult result)
    {
        lock (_lock)
        {
            _lastCycleAtUtc = result.CompletedAtUtc;
            _lastCycleProjectId = result.ProjectId;
            _lastDispatched = result.DispatchedCount;
            _lastFailures = result.FailureCount;
            _lastSkipped = result.SkippedDueToConcurrencyLimit;
            _totalDispatched += result.DispatchedCount;
            _totalFailures += result.FailureCount;
        }
    }

    public SchedulerStatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new SchedulerStatusSnapshot(
                _options.Enabled,
                _options.MaxConcurrentTasks,
                _lastCycleAtUtc,
                _lastCycleProjectId,
                _lastDispatched,
                _lastFailures,
                _lastSkipped,
                _totalDispatched,
                _totalFailures);
        }
    }
}
