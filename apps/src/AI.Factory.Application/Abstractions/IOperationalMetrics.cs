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

    void RecordRetryAttempt();

    void RecordRetrySuccess();

    void RecordRetryExhausted();

    void RecordRetryCancelled();

    void RecordAttemptTimeout();

    void RecordOperationTimeout();

    void RecordCircuitOpened();

    void RecordCircuitClosed();

    void RecordCircuitHalfOpen();

    void RecordCircuitRejected();

    void RecordBulkheadEntered();

    void RecordBulkheadQueued();

    void RecordBulkheadRejected();

    void RecordBulkheadCompleted();

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
    long? TotalRepairDurationMs,
    long RetryAttempts = 0,
    long RetrySucceeded = 0,
    long RetryExhausted = 0,
    long RetryCancelled = 0,
    long AttemptTimeouts = 0,
    long OperationTimeouts = 0,
    long CircuitOpened = 0,
    long CircuitClosed = 0,
    long CircuitHalfOpen = 0,
    long CircuitRejected = 0,
    long BulkheadEntered = 0,
    long BulkheadQueued = 0,
    long BulkheadRejected = 0,
    long BulkheadCompleted = 0);

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

    private long _retryAttempts;
    private long _retrySucceeded;
    private long _retryExhausted;
    private long _retryCancelled;
    private long _attemptTimeouts;
    private long _operationTimeouts;

    private long _circuitOpened;
    private long _circuitClosed;
    private long _circuitHalfOpen;
    private long _circuitRejected;

    private long _bulkheadEntered;
    private long _bulkheadQueued;
    private long _bulkheadRejected;
    private long _bulkheadCompleted;

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

    public void RecordRetryAttempt() => Interlocked.Increment(ref _retryAttempts);

    public void RecordRetrySuccess() => Interlocked.Increment(ref _retrySucceeded);

    public void RecordRetryExhausted() => Interlocked.Increment(ref _retryExhausted);

    public void RecordRetryCancelled() => Interlocked.Increment(ref _retryCancelled);

    public void RecordAttemptTimeout() => Interlocked.Increment(ref _attemptTimeouts);

    public void RecordOperationTimeout() => Interlocked.Increment(ref _operationTimeouts);

    public void RecordCircuitOpened() => Interlocked.Increment(ref _circuitOpened);

    public void RecordCircuitClosed() => Interlocked.Increment(ref _circuitClosed);

    public void RecordCircuitHalfOpen() => Interlocked.Increment(ref _circuitHalfOpen);

    public void RecordCircuitRejected() => Interlocked.Increment(ref _circuitRejected);

    public void RecordBulkheadEntered() => Interlocked.Increment(ref _bulkheadEntered);

    public void RecordBulkheadQueued() => Interlocked.Increment(ref _bulkheadQueued);

    public void RecordBulkheadRejected() => Interlocked.Increment(ref _bulkheadRejected);

    public void RecordBulkheadCompleted() => Interlocked.Increment(ref _bulkheadCompleted);

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
            _repairDuration.Total,
            Interlocked.Read(ref _retryAttempts),
            Interlocked.Read(ref _retrySucceeded),
            Interlocked.Read(ref _retryExhausted),
            Interlocked.Read(ref _retryCancelled),
            Interlocked.Read(ref _attemptTimeouts),
            Interlocked.Read(ref _operationTimeouts),
            Interlocked.Read(ref _circuitOpened),
            Interlocked.Read(ref _circuitClosed),
            Interlocked.Read(ref _circuitHalfOpen),
            Interlocked.Read(ref _circuitRejected),
            Interlocked.Read(ref _bulkheadEntered),
            Interlocked.Read(ref _bulkheadQueued),
            Interlocked.Read(ref _bulkheadRejected),
            Interlocked.Read(ref _bulkheadCompleted));
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
