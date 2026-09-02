namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Thread-safe in-process counters/timers — no Prometheus, no persisted raw
/// samples. A restart resets everything; this is a live operational
/// snapshot, not a historical record (the audit trail is that).
/// </summary>
public interface IOperationalMetrics
{
    void RecordOperationStarted();

    void RecordOperationCompleted(bool success);

    void RecordAgentExecution(bool success, long? durationMs);

    void RecordBuildExecution(bool success, long? durationMs);

    void RecordTestExecution(bool success, long? durationMs);

    void RecordRepairAttempt(bool success, long? durationMs);

    void RecordApproval(bool approved);

    OperationalMetricsSnapshot GetSnapshot();
}

public sealed record OperationalMetricsSnapshot(
    DateTime GeneratedAtUtc,
    long OperationsStarted,
    long OperationsSucceeded,
    long OperationsFailed,
    long AgentExecutions,
    long AgentFailures,
    long BuildExecutions,
    long BuildFailures,
    long TestExecutions,
    long TestFailures,
    long RepairAttempts,
    long RepairSuccesses,
    long RepairFailures,
    long ApprovalApproved,
    long ApprovalRejected,
    double? AverageAgentExecutionDurationMs,
    double? AverageBuildDurationMs,
    double? AverageTestDurationMs,
    double? AverageRepairDurationMs,
    long? TotalAgentExecutionDurationMs,
    long? TotalBuildDurationMs,
    long? TotalTestDurationMs,
    long? TotalRepairDurationMs);

public sealed class OperationalMetrics : IOperationalMetrics
{
    private long _operationsStarted;
    private long _operationsSucceeded;
    private long _operationsFailed;

    private long _agentExecutions;
    private long _agentFailures;
    private readonly DurationAccumulator _agentDuration = new();

    private long _buildExecutions;
    private long _buildFailures;
    private readonly DurationAccumulator _buildDuration = new();

    private long _testExecutions;
    private long _testFailures;
    private readonly DurationAccumulator _testDuration = new();

    private long _repairAttempts;
    private long _repairSuccesses;
    private long _repairFailures;
    private readonly DurationAccumulator _repairDuration = new();

    private long _approvalApproved;
    private long _approvalRejected;

    public void RecordOperationStarted() => Interlocked.Increment(ref _operationsStarted);

    public void RecordOperationCompleted(bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref _operationsSucceeded);
        }
        else
        {
            Interlocked.Increment(ref _operationsFailed);
        }
    }

    public void RecordAgentExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _agentExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _agentFailures);
        }

        _agentDuration.Add(durationMs);
    }

    public void RecordBuildExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _buildExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _buildFailures);
        }

        _buildDuration.Add(durationMs);
    }

    public void RecordTestExecution(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _testExecutions);

        if (!success)
        {
            Interlocked.Increment(ref _testFailures);
        }

        _testDuration.Add(durationMs);
    }

    public void RecordRepairAttempt(bool success, long? durationMs)
    {
        Interlocked.Increment(ref _repairAttempts);

        if (success)
        {
            Interlocked.Increment(ref _repairSuccesses);
        }
        else
        {
            Interlocked.Increment(ref _repairFailures);
        }

        _repairDuration.Add(durationMs);
    }

    public void RecordApproval(bool approved)
    {
        if (approved)
        {
            Interlocked.Increment(ref _approvalApproved);
        }
        else
        {
            Interlocked.Increment(ref _approvalRejected);
        }
    }

    public OperationalMetricsSnapshot GetSnapshot()
    {
        return new OperationalMetricsSnapshot(
            DateTime.UtcNow,
            Interlocked.Read(ref _operationsStarted),
            Interlocked.Read(ref _operationsSucceeded),
            Interlocked.Read(ref _operationsFailed),
            Interlocked.Read(ref _agentExecutions),
            Interlocked.Read(ref _agentFailures),
            Interlocked.Read(ref _buildExecutions),
            Interlocked.Read(ref _buildFailures),
            Interlocked.Read(ref _testExecutions),
            Interlocked.Read(ref _testFailures),
            Interlocked.Read(ref _repairAttempts),
            Interlocked.Read(ref _repairSuccesses),
            Interlocked.Read(ref _repairFailures),
            Interlocked.Read(ref _approvalApproved),
            Interlocked.Read(ref _approvalRejected),
            _agentDuration.Average,
            _buildDuration.Average,
            _testDuration.Average,
            _repairDuration.Average,
            _agentDuration.Total,
            _buildDuration.Total,
            _testDuration.Total,
            _repairDuration.Total);
    }

    /// <summary>Count + running total, so average is derived rather than stored — no raw samples kept.</summary>
    private sealed class DurationAccumulator
    {
        private long _count;
        private long _totalMs;

        public void Add(long? durationMs)
        {
            if (durationMs is null)
            {
                return;
            }

            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalMs, durationMs.Value);
        }

        public long? Total => Interlocked.Read(ref _count) == 0 ? null : Interlocked.Read(ref _totalMs);

        public double? Average
        {
            get
            {
                var count = Interlocked.Read(ref _count);
                return count == 0 ? null : (double)Interlocked.Read(ref _totalMs) / count;
            }
        }
    }
}
